using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Attention;
using ShiftSoftware.ShiftEntity.EFCore.Entities;

namespace ShiftSoftware.ShiftEntity.EFCore.Attention;

internal sealed class PendingIndexedSignal
{
    public required string EntityTypeName { get; init; }
    public required EntityEntry Entry { get; init; }
    public required StoredAttentionSignal Signal { get; init; }
}

internal static class AttentionPipeline
{
    internal static async Task<List<PendingIndexedSignal>?> ProcessEntity<TEntity>(
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
                RaisedAt = now,
            });
        }

        // Persist new signals
        List<PendingIndexedSignal>? pendingIndexed = null;
        var allSignals = existingSignals.Concat(newStoredSignals).ToList();

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

        // Update summary columns
        UpdateSummaryColumns(attentionEntity, allSignals);

        return pendingIndexed;
    }

    internal static async Task ClearSignals(
        ShiftDbContext db,
        string entityTypeName,
        long entityId,
        long? userId)
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

        if (isIndexed)
        {
            var signalEntries = await db.Set<AttentionSignalEntry>()
                .Where(x => x.EntityType == entityTypeName && x.EntityId == entityId && x.ClearedAt == null)
                .ToListAsync();

            foreach (var signal in signalEntries)
            {
                signal.ClearedAt = now;
                signal.ClearedByUserId = userId;
            }
        }
        else
        {
            var dbEntry = db.Entry(entity);
            var json = (string?)dbEntry.Property(AttentionSignalJsonHelper.ShadowPropertyName).CurrentValue;
            var signals = AttentionSignalJsonHelper.Deserialize(json);

            var updatedSignals = signals
                .Select(s => s.ClearedAt is null ? s with { ClearedAt = now, ClearedByUserId = userId } : s)
                .ToList();

            dbEntry.Property(AttentionSignalJsonHelper.ShadowPropertyName).CurrentValue =
                AttentionSignalJsonHelper.Serialize(updatedSignals);
        }

        if (entity is IHasAttention attentionEntity)
        {
            attentionEntity.HasActiveAttention = false;
            attentionEntity.HighestSeverity = null;
            attentionEntity.ActiveSignalCount = 0;
        }

        await db.SaveChangesAsync();
    }

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
