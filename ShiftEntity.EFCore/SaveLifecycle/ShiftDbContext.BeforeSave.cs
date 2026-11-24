using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Attention;
using ShiftSoftware.ShiftEntity.EFCore.Attention;
using System.Runtime.InteropServices;

namespace ShiftSoftware.ShiftEntity.EFCore;

// The before-save stage: what a save does with the tracked rows before anything is written.
//
// In rounds, for the rows that are new in each: audit stamping, then the before-save handlers. A handler may add,
// change or remove tracked rows, so after a round in which one ran the context detects changes again and offers the
// rows that are new. A row is offered once per save for each kind of change.
//
// Then once, on the rows as every handler left them: the repository's own step (the unique hash), and the attention
// evaluators. The replication trees and the look that decides about the transaction follow in the save itself.
public abstract partial class ShiftDbContext
{
    // What the stamper writes, worked out once per save, when the first row needs it.
    private struct StampValues
    {
        public bool Resolved;
        public DateTimeOffset Now;
        public long? UserId, CountryId, RegionId, CityId, CompanyId, BranchId;
    }

    // What the looks of one save at the tracker have found. The lists hold what the last look found, which is how
    // the rows stand once no handler has anything left to do. A struct, so that a save with nothing to note allocates
    // nothing for it.
    private struct BeforeSaveLook
    {
        public StampValues Stamp;

        // Whether the save has a row to write.
        public bool HasChanges;

        // Whether one of those rows is of a type that an observer wants or a handler after the write has. When none
        // is, the look before the write has nothing to find, and is skipped.
        public bool AfterTheWrite;

        // The rows that are new in the round, for the handlers that have them.
        public List<PendingRow>? Round;

        // Which kinds of change each record was already offered with: bit 0 added, 1 modified, 2 deleted.
        public Dictionary<object, byte>? Offered;

        // The added and modified rows that take part in attention.
        public List<EntityEntry>? Attention;

        // The added and modified rows of the repository's own type, for its step.
        public List<EntityEntry>? OfRepository;

        // The types of rows whose owned parts changed while the row itself looks unchanged.
        public HashSet<TypeNeeds>? OwnersOfChangedParts;
    }

    private async ValueTask<(AttentionSave? Attention, bool Detected, bool? HasChanges)> RunBeforeSaveAsync(
        SaveFrame frame, bool acceptAllChangesOnSuccess, CancellationToken cancellationToken)
    {
        AttentionOptions? attentionOptions = null;

        // A save that doesn't accept its changes may be sent again by its caller, so nothing is evaluated for it.
        if (acceptAllChangesOnSuccess && !AttentionSuppressed && ModelHasAttention)
        {
            attentionOptions = ServicesOfSave.GetService<IOptions<AttentionOptions>>()?.Value ?? new AttentionOptions();

            if (attentionOptions.EvaluateOn == AttentionSaves.Repository && repositorySaves == 0)
                attentionOptions = null;
        }

        var repository = frame.Repository;

        // A repository's save is stamped whatever the switch says. Any other save fills the new rows unless it is on.
        var stampsNewRows = repository is not null || !AuditStampingSuppressed;
        var handlers = SaveHandlers is { HasBeforeSave: true } registry ? registry : null;

        if (!stampsNewRows && attentionOptions is null && handlers is null)
            return (null, false, null);

        var look = new BeforeSaveLook();
        var findAttention = attentionOptions is not null;
        AttentionSave? attention = null;

        // Whether anything acts after the write in this host. Only then is it worth noting which rows it acts on.
        var lookAhead = SaveHandlers is { HasAfterWrite: true } || SaveObservers.Length > 0;

        frame.Part = SavePart.BeforeSave;

        try
        {
            for (var round = 1; ; round++)
            {
                // The context's own detection: before the first round, and again after each round in which a handler
                // ran. Where the app has switched detection off, it marks its own changes, and so do its handlers.
                // HasChanges detects where detection is on, and then answers from EF's count of changed rows.
                var any = ChangeTracker.HasChanges();

                // A save with nothing to write has nothing to stamp and nothing for a handler. The tracker is not
                // walked for it, however many unchanged rows it holds.
                if (round == 1 && !any)
                    break;

                // A row that EF would delete by cascade during its write is found here, so that the handlers get it.
                if (round == 1 && handlers is not null)
                    ResolveCascades();

                LookBeforeSave(frame, ref look, handlers, stampsNewRows, findAttention, lookAhead);

                if (handlers is null || look.Round is not { Count: > 0 } rows)
                    break;

                if (round > SaveUnit.Limit)
                    throw new InvalidOperationException(
                        $"The before-save handlers of {GetType().Name} are still producing rows after {SaveUnit.Limit} rounds: " +
                        $"{string.Join(", ", rows.Select(row => row.EntityType.Name).Distinct())}. Handlers that add or change a row in " +
                        "every round never let the save write. The handlers of those rows: " +
                        $"{string.Join(", ", rows.Select(row => row.EntityType).Distinct().SelectMany(type => handlers.For(type).For(SaveStage.BeforeSave)).Distinct().Select(call => call.Name))}.");

                if (!await RunBeforeSaveHandlersAsync(frame, handlers, rows, cancellationToken))
                    break;
            }

            // Once, on the rows as the handlers left them.
            frame.AfterHandlers = repository?.BeforeWrite?.Invoke(look.OfRepository is { } ofRepository ? ofRepository : []);

            if (look.Attention is { Count: > 0 } attentionRows)
            {
                // The evaluators see what every handler left. The rows they add, such as signal rows, are the
                // framework's bookkeeping: they are stamped, and no before-save handler is offered them.
                (attention, var added) = await EvaluateAttentionAsync(frame, attentionRows, attentionOptions!, stampsNewRows, cancellationToken);

                if (added is not null)
                {
                    foreach (var entry in added)
                        if (entry.State == EntityState.Added && entry.Entity is IShiftEntityAudit { AuditFieldsAreSet: false } auditable)
                            Stamp(entry, auditable, isAdded: true, repository, ref look.Stamp);
                }
            }

            // The last look saw the rows as the write will find them, unless the save has rows that take part in
            // attention: the evaluators may have added rows after it.
            frame.NothingAfterTheWrite = lookAhead && look.HasChanges && !look.AfterTheWrite && look.Attention is not { Count: > 0 };
        }
        finally
        {
            frame.Part = SavePart.None;
        }

        return (attention, true, look.HasChanges);
    }

    // Resolves the cascades EF would resolve during its write. The public method forces a cascade even where the app
    // has set a timing to Never, where EF's own save throws instead, so it is left to EF's write then.
    private void ResolveCascades()
    {
        var tracker = ChangeTracker;

        if (tracker.CascadeDeleteTiming == CascadeTiming.Never || tracker.DeleteOrphansTiming == CascadeTiming.Never)
            return;

        // The changes were detected just before this. CascadeChanges would detect them a second time.
        var autoDetect = tracker.AutoDetectChangesEnabled;

        try
        {
            tracker.AutoDetectChangesEnabled = false;
            tracker.CascadeChanges();
        }
        finally
        {
            tracker.AutoDetectChangesEnabled = autoDetect;
        }
    }

    // One pass over the tracked rows, which detects nothing: the changes are detected already. It stamps the rows
    // that are not stamped yet, and notes the rows that are new for the before-save handlers. In a host without
    // before-save handlers it is the only pass, and costs what the pipeline's stamping pass always cost.
    private void LookBeforeSave(SaveFrame frame, ref BeforeSaveLook look, SaveHandlerRegistry? handlers, bool stampsNewRows, bool findAttention, bool lookAhead)
    {
        var repository = frame.Repository;
        var switches = frame.Switches;

        look.HasChanges = false;
        look.AfterTheWrite = false;
        look.Round?.Clear();
        look.Attention?.Clear();
        look.OfRepository?.Clear();
        look.OwnersOfChangedParts?.Clear();

        var autoDetect = ChangeTracker.AutoDetectChangesEnabled;

        try
        {
            ChangeTracker.AutoDetectChangesEnabled = false;

            foreach (var entry in ChangeTracker.Entries())
            {
                var state = entry.State;

                if (state is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
                    continue;

                look.HasChanges = true;
                var entity = entry.Entity;

                // An owned entry is part of its owner's row, so the owner's handlers count for it.
                if (lookAhead && !look.AfterTheWrite)
                {
                    var type = NeedsOf(entry.Metadata);
                    var row = type.Owner ?? type;

                    look.AfterTheWrite = type.Wanted != 0 || row.Wanted != 0 || row.Handlers.AnyAfterWrite;
                }

                if (state != EntityState.Deleted)
                {
                    // The stamper. New rows are filled where they were left unset. Modified rows get their last-save
                    // columns on a repository's save only. A row that was stamped already is left alone, which is
                    // also what keeps a later round from stamping it again.
                    if (entity is IShiftEntityAudit { AuditFieldsAreSet: false } auditable &&
                        (state == EntityState.Added ? stampsNewRows : repository is not null))
                        Stamp(entry, auditable, state == EntityState.Added, repository, ref look.Stamp);

                    // The rows this save writes that take part in attention. A row that changed in attention's own
                    // columns only is not a changed record.
                    if (findAttention && entity is IHasAttention &&
                        (state == EntityState.Added || !AttentionSave.IsOnlyBookkeeping(entry)))
                        (look.Attention ??= []).Add(entry);

                    if (repository is not null && repository.EntityType.IsInstanceOfType(entity))
                        (look.OfRepository ??= []).Add(entry);
                }

                if (handlers is null && repository is null)
                    continue;

                var needs = NeedsOf(entry.Metadata);

                // An owned entry is part of its owner's row. EF marks the owner modified only during its write, so the
                // owner is looked for below.
                if (needs.Owner is { } owner)
                {
                    if (owner.OwnedNavigations is not null &&
                        ((handlers is not null && owner.Handlers.For(SaveStage.BeforeSave).Length > 0) || (repository is not null && owner.IsAuditable)))
                        (look.OwnersOfChangedParts ??= []).Add(owner);

                    continue;
                }

                if (handlers is not null)
                    Offer(entry, state, needs, switches, ref look);
            }

            if (look.OwnersOfChangedParts is { Count: > 0 } owners)
                LookForOwnersOfChangedParts(frame, ref look, handlers, owners);
        }
        finally
        {
            ChangeTracker.AutoDetectChangesEnabled = autoDetect;
        }
    }

    // A row whose owned part alone changed is a modified row, although EF still shows it as unchanged.
    private void LookForOwnersOfChangedParts(SaveFrame frame, ref BeforeSaveLook look, SaveHandlerRegistry? handlers, HashSet<TypeNeeds> owners)
    {
        var repository = frame.Repository;

        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State != EntityState.Unchanged)
                continue;

            var needs = NeedsOf(entry.Metadata);

            if (!owners.Contains(needs) || !HasChangedPart(entry, needs))
                continue;

            if (repository is not null && entry.Entity is IShiftEntityAudit { AuditFieldsAreSet: false } auditable)
                Stamp(entry, auditable, isAdded: false, repository, ref look.Stamp);

            if (handlers is not null)
                Offer(entry, EntityState.Modified, needs, frame.Switches, ref look);
        }
    }

    // Whether an owned part of the row is about to be written: an owned reference or an item of an owned collection,
    // at any depth.
    private bool HasChangedPart(EntityEntry owner, TypeNeeds needs)
    {
        if (needs.OwnedNavigations is not { } navigations)
            return false;

        foreach (var navigation in navigations)
        {
            if (navigation.IsCollection)
            {
                var collection = owner.Collection(navigation);

                if (collection.CurrentValue is not { } items)
                    continue;

                foreach (var item in items)
                    if (collection.FindEntry(item) is { } part && IsChangedPart(part))
                        return true;
            }
            else if (owner.Reference(navigation).TargetEntry is { } part && IsChangedPart(part))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsChangedPart(EntityEntry part) =>
        part.State is EntityState.Added or EntityState.Modified or EntityState.Deleted || HasChangedPart(part, NeedsOf(part.Metadata));

    // Notes a row for the before-save handlers of its type, unless it was already offered with this kind of change.
    private void Offer(EntityEntry entry, EntityState state, TypeNeeds needs, SaveSwitches? switches, ref BeforeSaveLook look)
    {
        var calls = needs.Handlers.For(SaveStage.BeforeSave);

        if (calls.Length == 0)
            return;

        if (!needs.HandlersCanBeCreated)
            RefuseHandlersThatCannotBeCreated(needs, switches);

        if (switches is not null && !SaveSwitches.AnyOn(switches, calls))
            return;

        var change = state switch
        {
            EntityState.Added => RowChange.Added,
            EntityState.Deleted => RowChange.Deleted,
            _ => RowChange.Modified,
        };

        var kind = (byte)(1 << (int)change);
        ref var offered = ref CollectionsMarshal.GetValueRefOrAddDefault(look.Offered ??= new(ReferenceEqualityComparer.Instance), entry.Entity, out _);

        if ((offered & kind) != 0)
            return;

        offered |= kind;
        (look.Round ??= []).Add(new PendingRow(entry, change, needs.ClrType));
    }

    // Calls the before-save handlers that have a row among the round's, each once with its rows, in their order.
    // Returns whether a handler ran, which is when the tracked rows may have changed.
    private async ValueTask<bool> RunBeforeSaveHandlersAsync(SaveFrame frame, SaveHandlerRegistry handlers, List<PendingRow> rows,
        CancellationToken cancellationToken)
    {
        List<SaveHandlerCall>? calls = null;
        Type? lastType = null;

        foreach (var row in rows)
        {
            if (row.EntityType == lastType)
                continue;

            lastType = row.EntityType;

            foreach (var call in handlers.For(row.EntityType).For(SaveStage.BeforeSave))
                if (!SaveSwitches.IsOff(frame.Switches, call) && (calls is null || !calls.Contains(call)))
                    (calls ??= []).Add(call);
        }

        if (calls is null)
            return false;

        calls.Sort(static (a, b) => a.Rank.CompareTo(b.Rank));

        var unit = UnitOf(frame);
        var ran = false;

        foreach (var call in calls)
        {
            // The rows of this call among the round's. A row that an earlier handler of the round stopped tracking is
            // not written, so nobody is called for it any more.
            var picked = new int[rows.Count];
            var count = 0;
            lastType = null;
            var mine = false;

            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];

                if (row.EntityType != lastType)
                {
                    lastType = row.EntityType;
                    mine = call.RowType.IsAssignableFrom(lastType);
                }

                if (mine && row.Entry.State != EntityState.Detached)
                    picked[count++] = i;
            }

            if (count == 0)
                continue;

            ran = true;
            frame.BeforeSaveCall = call;
            var handler = CreateHandler(call, out var owned);

            try
            {
                await call.InvokeBeforeSaveAsync(handler, this, unit, new PendingRowView(rows, picked, count), frame.Repository is not null,
                    SaveSwitches.StagesOff(frame.Switches, call.Handler), cancellationToken);
            }
            finally
            {
                frame.BeforeSaveCall = null;

                if (owned)
                    await DisposeHandlerAsync(handler);
            }
        }

        return ran;
    }

    /// <summary>
    /// A before-save handler needs its reads, the write and everything after it to be one transaction. The context
    /// opens its own when the save is in none. Inside a caller's transaction it sets the save's savepoint here, so that
    /// a failure of the save also undoes what the handler wrote from here on, such as a counter it moved on with SQL of
    /// its own. The write then uses that savepoint and sets no other. Where no savepoint can be set, a failure of the
    /// save, before its write as after it, leaves the caller's transaction able only to roll back.
    /// </summary>
    /// <param name="member">What the handler called, as a message names it.</param>
    /// <param name="cancellationToken">The save's.</param>
    internal async ValueTask RequireTransactionAsync(string member, CancellationToken cancellationToken)
    {
        if (saveFrame is not { Part: SavePart.BeforeSave } frame)
            throw new InvalidOperationException(
                $"{member} asks for a transaction around the save a before-save handler is running in, so it can be called " +
                "only by that handler, while it runs.");

        // A provider that has no transactions.
        if (!Database.IsRelational())
            return;

        // The caller's transaction, or one the context opened for this save already, which rolls back whole.
        if (frame.InTransaction)
        {
            frame.AskedForTransaction |= frame.Own is null;
            await SetSavepointAsync(frame, frame.Async, cancellationToken);
            return;
        }

        var asked = frame.BeforeSaveCall is { } call ? $"{call.HandlerType.Name} asked for" : "a before-save handler asked for";
        RefuseATransactionOfItsOwn($"rows that {asked} a transaction around ({member})");

        frame.Own = await Database.BeginForSaveAsync(UnitOf(frame), frame.Async, cancellationToken);
        frame.InTransaction = true;
    }

    // Runs the attention evaluators of the rows, and notes the rows they add to the context, for the stamper.
    private async ValueTask<(AttentionSave? Attention, List<EntityEntry>? Added)> EvaluateAttentionAsync(SaveFrame frame, List<EntityEntry> rows,
        AttentionOptions options, bool stampsNewRows, CancellationToken cancellationToken)
    {
        List<EntityEntry>? added = null;

        void Note(object? sender, EntityTrackedEventArgs e)
        {
            if (!e.FromQuery && e.Entry.State == EntityState.Added)
                (added ??= []).Add(e.Entry);
        }

        if (stampsNewRows)
            ChangeTracker.Tracked += Note;

        AttentionSave? attention;

        try
        {
            attention = await AttentionSave.EvaluateAsync(this, rows, ServicesOfSave, options, frame.Async, cancellationToken);
        }
        finally
        {
            if (stampsNewRows)
                ChangeTracker.Tracked -= Note;
        }

        return (attention, added);
    }

    // Stamps one row. A new row is filled where the caller left a column unset, so a value assigned by hand wins. A
    // modified row gets its last-save columns, written through EF's entry: the change is then marked at once, and does
    // not depend on a later pass of change detection.
    private void Stamp(EntityEntry entry, IShiftEntityAudit auditable, bool isAdded, RepositorySave? repository, ref StampValues values)
    {
        if (!values.Resolved)
        {
            // Resolved once, and only when there is a row to stamp. Every value is resolved defensively: a missing
            // claim service, or a value that cannot be read, is null and not an error, so a bare context still saves.
            var claims = repository?.Claims ?? TryResolveClaims();

            values.Now = DateTimeOffset.UtcNow;
            values.UserId = repository is not null ? repository.UserId : SafeClaim(claims, c => c.GetUserID());
            values.CountryId = SafeClaim(claims, c => c.GetCountryID());
            values.RegionId = SafeClaim(claims, c => c.GetRegionID());
            values.CityId = SafeClaim(claims, c => c.GetCityID());
            values.CompanyId = SafeClaim(claims, c => c.GetCompanyID());
            values.BranchId = SafeClaim(claims, c => c.GetCompanyBranchID());
            values.Resolved = true;
        }

        if (isAdded)
        {
            AuditStamper.StampAuditFields(auditable, isAdded: true, values.UserId, values.Now);
            AuditStamper.StampCreationClaims(entry.Entity, values.CountryId, values.RegionId, values.CityId, values.CompanyId, values.BranchId);
        }
        else
        {
            AuditStamper.StampLastSave(entry, auditable, values.UserId, values.Now);
        }
    }

    /// <summary>Resolves <see cref="IdentityClaimProvider"/> if available, returning null instead of throwing.</summary>
    private IdentityClaimProvider? TryResolveClaims()
    {
        try
        {
            if (attachedServices?.GetService<IdentityClaimProvider>() is { } attached)
                return attached;

            // Where EF's accessor looks, in its order: the context's own services, then the application's. Asked of
            // the providers themselves, because the accessor throws for a service that is not registered, and a host
            // without a claim provider would throw and catch that in every save that stamps a new row.
            var own = ((IInfrastructure<IServiceProvider>)this).Instance;

            return own.GetService<IdentityClaimProvider>()
                ?? own.GetService<IDbContextOptions>()?.FindExtension<CoreOptionsExtension>()?.ApplicationServiceProvider?.GetService<IdentityClaimProvider>();
        }
        catch { return null; }
    }

    private static long? SafeClaim(IdentityClaimProvider? claims, Func<IdentityClaimProvider, long?> get)
    {
        if (claims is null)
            return null;

        try { return get(claims); }
        catch { return null; }
    }
}
