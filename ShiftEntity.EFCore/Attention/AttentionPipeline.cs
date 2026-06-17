using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Attention;
using ShiftSoftware.ShiftEntity.EFCore.Entities;

namespace ShiftSoftware.ShiftEntity.EFCore.Attention;

/// <summary>
/// Holds an indexed-mode signal whose entity has not yet been assigned a database ID
/// (Insert path). Flushed after <c>SaveChangesAsync</c> assigns the ID.
/// </summary>
internal sealed class PendingIndexedSignal
{
    /// <summary>CLR type name of the entity that raised the signal.</summary>
    public required string EntityTypeName { get; init; }

    /// <summary>EF Core change-tracker entry; used to read the assigned ID after save.</summary>
    public required EntityEntry Entry { get; init; }

    /// <summary>The signal to persist once the entity ID is known.</summary>
    public required StoredAttentionSignal Signal { get; init; }
}

/// <summary>
/// Outcome of <see cref="AttentionPipeline.ProcessEntity"/> for one entity that raised at
/// least one new signal: the newly-raised (post-dedup) signals — the set that becomes
/// <see cref="AttentionRaised"/> events once the save commits — plus, in indexed mode on
/// Insert, the signals still awaiting a database ID.
/// </summary>
internal sealed class AttentionEntityOutcome
{
    /// <summary>CLR type name of the entity that raised the signals.</summary>
    public required string EntityTypeName { get; init; }

    /// <summary>
    /// EF Core change-tracker entry; the repository reads the entity ID from it after save
    /// (Insert entities only receive their ID then) when materializing events.
    /// </summary>
    public required EntityEntry Entry { get; init; }

    /// <summary>Newly-raised (post-dedup) signals — one event each, published after commit.</summary>
    public required List<StoredAttentionSignal> NewSignals { get; init; }

    /// <summary>
    /// Indexed-mode signals pending entity ID assignment (Insert path), flushed via
    /// <see cref="AttentionPipeline.FlushPendingSignals"/>. <c>null</c> when none.
    /// </summary>
    public List<PendingIndexedSignal>? PendingIndexed { get; init; }
}

/// <summary>
/// Core evaluation and persistence engine for the attention system. Invoked by
/// <c>ShiftRepository.ProcessEntriesAndSave</c> for each <see cref="IHasAttention"/> entity.
/// Discovers evaluators, runs them, deduplicates signals, persists results, and updates
/// the entity's summary columns — all within the same <c>SaveChanges</c> transaction.
/// </summary>
internal static class AttentionPipeline
{
    /// <summary>
    /// Runs all registered evaluators against the entity, deduplicates raised signals,
    /// persists new signals in the entity's storage mode, and updates summary columns.
    /// </summary>
    /// <returns>
    /// The outcome carrying the newly-raised (post-dedup) signals — the repository turns
    /// them into <see cref="AttentionRaised"/> events after the save commits — and any
    /// pending indexed signals for Insert entities (flushed via
    /// <see cref="FlushPendingSignals"/> after <c>SaveChangesAsync</c> assigns database
    /// IDs). <c>null</c> when no new signal was raised.
    /// </returns>
    internal static async Task<AttentionEntityOutcome?> ProcessEntity<TEntity>(
        ShiftDbContext db,
        EntityEntry entry,
        TEntity entity,
        TEntity? original,
        ActionTypes action,
        IServiceProvider services) where TEntity : class
    {
        if (entity is not IHasAttention attentionEntity)
            return null;

        var clrType = entity.GetType();
        var entityTypeName = clrType.Name;
        var isIndexed = entity is IHasIndexedAttention;
        var isInsert = action == ActionTypes.Insert;

        var options = services.GetService<IOptions<AttentionOptions>>()?.Value ?? new AttentionOptions();

        var entityId = isInsert ? 0L : (long)entry.Property("ID").CurrentValue!;
        var existingSignals = await LoadExistingSignals(db, entry, entityTypeName, isIndexed, entityId);

        var rawSignals = new List<(AttentionSignal signal, string defaultSource)>();

        // Source 1: entity's own evaluator
        if (entity is IHasAttentionEvaluator<TEntity> selfEvaluator)
        {
            var context = new AttentionContext<TEntity>
            {
                Action = action,
                Entity = entity,
                Original = original,
                Services = services,
            };
            var signal = selfEvaluator.EvaluateAttention(context);
            if (signal is not null)
                rawSignals.Add((signal, clrType.Name));
        }

        // Sources 2 & 3: DI-registered evaluators (direct + capability-bound)
        var descriptors = services.GetService<IEnumerable<AttentionEvaluatorDescriptor>>();
        if (descriptors is not null)
        {
            foreach (var descriptor in descriptors)
            {
                if (!descriptor.TargetType.IsAssignableFrom(clrType))
                    continue;

                AttentionSignal? signal;

                if (descriptor.RequiresHistory && descriptor.InvokeWithHistory is not null)
                {
                    var history = existingSignals
                        .Where(s => s.Source == descriptor.EvaluatorTypeName)
                        .ToList();
                    signal = descriptor.InvokeWithHistory(services, entity, original, action, history);
                }
                else
                {
                    signal = descriptor.Invoke(services, entity, original, action);
                }

                if (signal is not null)
                    rawSignals.Add((signal, descriptor.EvaluatorTypeName));
            }
        }

        if (rawSignals.Count == 0 && existingSignals.Count == 0)
            return null;

        var now = DateTimeOffset.UtcNow;

        var newStoredSignals = new List<StoredAttentionSignal>();
        foreach (var (signal, defaultSource) in rawSignals)
        {
            var source = signal.Source ?? defaultSource;

            if (IsDuplicate(source, signal.Category, existingSignals, options.ReRaiseWindow, now))
                continue;

            newStoredSignals.Add(new StoredAttentionSignal
            {
                Source = source,
                Category = signal.Category,
                Reason = signal.Reason,
                Severity = signal.Severity,
                PayloadJson = signal.PayloadJson,
                ClearScope = signal.ClearScope,
                RaisedAt = now,
            });
        }

        // Persist new signals
        List<PendingIndexedSignal>? pendingIndexed = null;
        var allSignals = existingSignals.Concat(newStoredSignals).ToList();

        if (newStoredSignals.Count > 0)
        {
            if (isIndexed)
            {
                foreach (var stored in newStoredSignals)
                {
                    if (isInsert)
                    {
                        pendingIndexed ??= [];
                        pendingIndexed.Add(new PendingIndexedSignal
                        {
                            EntityTypeName = entityTypeName,
                            Entry = entry,
                            Signal = stored,
                        });
                    }
                    else
                    {
                        db.Set<AttentionSignalEntry>().Add(
                            AttentionSignalEntry.FromStoredSignal(stored, entityTypeName, entityId));
                    }
                }
            }
            else
            {
                entry.Property(AttentionSignalJsonHelper.ShadowPropertyName).CurrentValue =
                    AttentionSignalJsonHelper.Serialize(allSignals);
            }
        }

        // Update summary columns
        UpdateSummaryColumns(attentionEntity, allSignals);

        if (newStoredSignals.Count == 0)
            return null;

        return new AttentionEntityOutcome
        {
            EntityTypeName = entityTypeName,
            Entry = entry,
            NewSignals = newStoredSignals,
            PendingIndexed = pendingIndexed,
        };
    }

    /// <summary>
    /// Marks the active signals selected by <paramref name="filter"/> for the specified entity as
    /// cleared, <em>recomputes</em> the entity's summary columns from whatever stays active, and
    /// saves. Works against both JSON-shadow and indexed storage modes. A <c>null</c> filter clears
    /// every active signal (the historical behavior); a scoped or per-signal filter may leave other
    /// signals active, which is why the summary columns are recomputed rather than zeroed.
    /// </summary>
    /// <returns>
    /// The entity's <c>LastSaveDate</c> after the save — when the clear modifies the entity row,
    /// this method stamps the last-save columns itself (the context's SaveChanges backfill is
    /// insert-only), and the stamp doubles as the optimistic-concurrency version.
    /// Endpoints return it (<see cref="ClearAttentionResponse"/>) so clients holding a
    /// pre-clear DTO can patch it instead of hitting a version conflict on their next save.
    /// A clear that matched nothing leaves the entity row (and its stamp) untouched and returns the
    /// current stamp. <c>null</c> when the entity doesn't carry audit fields.
    /// </returns>
    internal static async Task<DateTimeOffset?> ClearSignals(
        ShiftDbContext db,
        string entityTypeName,
        long entityId,
        long? userId,
        AttentionClearFilter? filter = null)
    {
        var entityType = db.Model.GetEntityTypes()
            .FirstOrDefault(e => e.ClrType.Name == entityTypeName);

        if (entityType is null)
            throw new InvalidOperationException($"Entity type '{entityTypeName}' not found in the model.");

        var clrType = entityType.ClrType;

        if (!typeof(IHasAttention).IsAssignableFrom(clrType))
            throw new InvalidOperationException($"Entity type '{entityTypeName}' does not implement IHasAttention.");

        var isIndexed = typeof(IHasIndexedAttention).IsAssignableFrom(clrType);
        var now = DateTimeOffset.UtcNow;

        var entity = await db.FindAsync(clrType, entityId)
            ?? throw new InvalidOperationException($"Entity '{entityTypeName}' with ID {entityId} not found.");

        // Signals that survive this clear — they drive the recomputed summary columns. A scoped or
        // per-signal filter can leave some active, so we never blindly zero the columns.
        List<StoredAttentionSignal> remainingActive;
        var clearedAny = false;

        if (isIndexed)
        {
            var activeEntries = await db.Set<AttentionSignalEntry>()
                .Where(x => x.EntityType == entityTypeName && x.EntityId == entityId && x.ClearedAt == null)
                .ToListAsync();

            foreach (var signalEntry in activeEntries)
            {
                if (filter is not null && !filter.Matches(signalEntry.ToStoredSignal()))
                    continue;

                signalEntry.ClearedAt = now;
                signalEntry.ClearedByUserId = userId;
                clearedAny = true;
            }

            remainingActive = activeEntries
                .Where(x => x.ClearedAt is null)
                .Select(x => x.ToStoredSignal())
                .ToList();
        }
        else
        {
            var dbEntry = db.Entry(entity);
            var json = (string?)dbEntry.Property(AttentionSignalJsonHelper.ShadowPropertyName).CurrentValue;
            var signals = AttentionSignalJsonHelper.Deserialize(json);

            var updatedSignals = signals
                .Select(s =>
                {
                    if (s.ClearedAt is not null) return s;                  // already cleared
                    if (filter is not null && !filter.Matches(s)) return s; // not selected by the filter
                    clearedAny = true;
                    return s with { ClearedAt = now, ClearedByUserId = userId };
                })
                .ToList();

            // Only rewrite the shadow property when something actually changed, so a no-op clear
            // leaves the entity row (and its concurrency stamp) untouched.
            if (clearedAny)
                dbEntry.Property(AttentionSignalJsonHelper.ShadowPropertyName).CurrentValue =
                    AttentionSignalJsonHelper.Serialize(updatedSignals);

            remainingActive = updatedSignals.Where(s => s.ClearedAt is null).ToList();
        }

        // Recompute the summary columns from what stayed active, and advance the audit stamp — but
        // only when the clear actually cleared something. A no-op clear must keep the current stamp,
        // since the returned value doubles as the client's optimistic-concurrency version.
        if (clearedAny && entity is IHasAttention attentionEntity)
        {
            attentionEntity.HasActiveAttention = remainingActive.Count > 0;
            attentionEntity.HighestSeverity = remainingActive.Count > 0 ? remainingActive.Max(s => s.Severity) : null;
            attentionEntity.ActiveSignalCount = remainingActive.Count;
        }

        if (clearedAny && entity is IShiftEntityAudit auditable && db.Entry(entity).State == EntityState.Modified)
            AuditStamper.StampAuditFields(auditable, isAdded: false, userId, now);

        await db.SaveChangesAsync();

        return (entity as IShiftEntityAudit)?.LastSaveDate;
    }

    /// <summary>
    /// Persists pending indexed signals after <c>SaveChangesAsync</c> has assigned database IDs
    /// to Insert entities. Called in a second save pass by the repository.
    /// </summary>
    internal static void FlushPendingSignals(ShiftDbContext db, List<PendingIndexedSignal> pending)
    {
        foreach (var p in pending)
        {
            var entityId = (long)p.Entry.Property("ID").CurrentValue!;
            db.Set<AttentionSignalEntry>().Add(
                AttentionSignalEntry.FromStoredSignal(p.Signal, p.EntityTypeName, entityId));
        }
    }

    private static async Task<List<StoredAttentionSignal>> LoadExistingSignals(
        ShiftDbContext db,
        EntityEntry entry,
        string entityTypeName,
        bool isIndexed,
        long entityId)
    {
        if (isIndexed)
        {
            if (entityId == 0)
                return [];

            var entries = await db.Set<AttentionSignalEntry>()
                .Where(x => x.EntityType == entityTypeName && x.EntityId == entityId)
                .ToListAsync();

            return entries.Select(x => x.ToStoredSignal()).ToList();
        }

        var json = (string?)entry.Property(AttentionSignalJsonHelper.ShadowPropertyName).CurrentValue;
        return AttentionSignalJsonHelper.Deserialize(json);
    }

    private static bool IsDuplicate(
        string source,
        string category,
        List<StoredAttentionSignal> existingSignals,
        TimeSpan reRaiseWindow,
        DateTimeOffset now)
    {
        return existingSignals.Any(existing =>
            existing.Source == source &&
            existing.Category == category &&
            existing.ClearedAt is null &&
            (reRaiseWindow == TimeSpan.MaxValue || existing.RaisedAt + reRaiseWindow > now));
    }

    private static void UpdateSummaryColumns(IHasAttention entity, List<StoredAttentionSignal> allSignals)
    {
        var active = allSignals.Where(s => s.ClearedAt is null).ToList();
        entity.HasActiveAttention = active.Count > 0;
        entity.HighestSeverity = active.Count > 0 ? active.Max(s => s.Severity) : null;
        entity.ActiveSignalCount = active.Count;
    }
}
