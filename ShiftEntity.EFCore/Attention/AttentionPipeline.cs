using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Attention;
using ShiftSoftware.ShiftEntity.EFCore.Entities;
using System.Collections.Concurrent;
using System.Reflection;

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
    /// EF Core change-tracker entry; the save reads the entity ID from it after it has written
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
/// Core evaluation and persistence engine for the attention system. The save pipeline of
/// <see cref="ShiftDbContext"/> invokes it, through <see cref="AttentionSave"/>, for each added or
/// modified <see cref="IHasAttention"/> row before a save writes. It discovers evaluators, runs them,
/// deduplicates signals, persists results, and updates the entity's summary columns, all in the
/// same save.
/// </summary>
internal static class AttentionPipeline
{
    /// <summary>
    /// Runs all registered evaluators against the entity, deduplicates raised signals,
    /// persists new signals in the entity's storage mode, and updates summary columns.
    /// </summary>
    /// <param name="db">The context about to save.</param>
    /// <param name="entry">The added or modified row.</param>
    /// <param name="services">The save's services, for evaluators registered in DI.</param>
    /// <param name="options">The host's attention options.</param>
    /// <param name="indexedSignals">
    /// The stored signals of an indexed row that already exists, loaded by the caller for all the
    /// save's rows at once. Null for a new row and for a row in JSON mode.
    /// </param>
    /// <returns>
    /// The outcome carrying the newly-raised (post-dedup) signals, which become
    /// <see cref="AttentionRaised"/> events after the save commits, and any pending indexed
    /// signals for new rows (written by <see cref="FlushPendingSignals"/> once the save has
    /// assigned database IDs). <c>null</c> when no new signal was raised.
    /// </returns>
    internal static AttentionEntityOutcome? ProcessEntity(
        ShiftDbContext db,
        EntityEntry entry,
        IServiceProvider services,
        AttentionOptions options,
        IReadOnlyList<StoredAttentionSignal>? indexedSignals)
    {
        var entity = entry.Entity;

        if (entity is not IHasAttention attentionEntity)
            return null;

        var clrType = entity.GetType();
        var entityTypeName = clrType.Name;
        var isIndexed = entity is IHasIndexedAttention;
        var isInsert = entry.State == EntityState.Added;
        var action = isInsert ? ActionTypes.Insert : ActionTypes.Update;
        var original = isInsert ? null : entry.OriginalValues.ToObject();

        var entityId = isInsert ? 0L : (long)entry.Property("ID").CurrentValue!;

        List<StoredAttentionSignal> existingSignals = isIndexed
            ? indexedSignals is null ? [] : [.. indexedSignals]
            : AttentionSignalJsonHelper.Deserialize((string?)entry.Property(AttentionSignalJsonHelper.ShadowPropertyName).CurrentValue);

        var rawSignals = new List<(AttentionSignal signal, string defaultSource)>();

        // Source 1: the entity's own evaluators
        foreach (var evaluate in SelfEvaluatorsOf(clrType))
        {
            var signal = evaluate(entity, original, action, services);
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

        if (!isInsert)
            MarkSummaryColumnsModified(entry);

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

    // An entity names itself in IHasAttentionEvaluator<TSelf>, so each of its evaluators is called with the context
    // typed that way. Worked out once per entity class.
    private delegate AttentionSignal? SelfEvaluator(object entity, object? original, ActionTypes action, IServiceProvider services);

    private static readonly ConcurrentDictionary<Type, SelfEvaluator[]> selfEvaluators = new();

    private static readonly MethodInfo evaluateSelfMethod =
        typeof(AttentionPipeline).GetMethod(nameof(EvaluateSelf), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static SelfEvaluator[] SelfEvaluatorsOf(Type clrType) => selfEvaluators.GetOrAdd(clrType, type => type.GetInterfaces()
        .Where(x => x.IsGenericType && x.GetGenericTypeDefinition() == typeof(IHasAttentionEvaluator<>))
        .Select(x => evaluateSelfMethod.MakeGenericMethod(x.GetGenericArguments()[0]).CreateDelegate<SelfEvaluator>())
        .ToArray());

    private static AttentionSignal? EvaluateSelf<TSelf>(object entity, object? original, ActionTypes action, IServiceProvider services)
        where TSelf : class
        => ((IHasAttentionEvaluator<TSelf>)entity).EvaluateAttention(new AttentionContext<TSelf>
        {
            Action = action,
            Entity = (TSelf)entity,
            Original = original as TSelf,
            Services = services,
        });

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

        // Acknowledging is not a change of the record. Evaluating here would raise a state-based signal again at once.
        using (db.SuppressAttention())
            await db.SaveChangesAsync();

        return (entity as IShiftEntityAudit)?.LastSaveDate;
    }

    /// <summary>
    /// Adds the pending indexed signals once the save has assigned database IDs to the new rows.
    /// The save pipeline writes them in a second pass, in the same transaction.
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

    private static readonly string[] SummaryColumns =
        [nameof(IHasAttention.HasActiveAttention), nameof(IHasAttention.HighestSeverity), nameof(IHasAttention.ActiveSignalCount)];

    // The summary columns of a row that already exists are written only when EF knows they changed. The evaluators run
    // in every save now, also in one whose caller switched change detection off, so EF is told here, and not left to
    // find out.
    private static void MarkSummaryColumnsModified(EntityEntry entry)
    {
        foreach (var name in SummaryColumns)
        {
            if (entry.Metadata.FindProperty(name) is null)
                continue;

            var property = entry.Property(name);

            if (!property.IsModified && !Equals(property.CurrentValue, property.OriginalValue))
                property.IsModified = true;
        }
    }
}
