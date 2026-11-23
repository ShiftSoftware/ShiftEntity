using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging;
using ShiftSoftware.ShiftEntity.Core.Attention;
using ShiftSoftware.ShiftEntity.EFCore.Entities;
using System.Runtime.CompilerServices;

namespace ShiftSoftware.ShiftEntity.EFCore.Attention;

/// <summary>
/// What one save of a <see cref="ShiftDbContext"/> means for attention. Before the save writes, it runs the evaluators
/// of every added or modified <see cref="IHasAttention"/> row, whoever saves it: a repository, a service or a function
/// on the plain context. After the write it adds the signals that had to wait for a new row's key, and once the save has
/// committed it publishes one <see cref="AttentionRaised"/> for each new signal.
/// </summary>
/// <remarks>
/// It lives in the context's save pipeline, and not in the repository, so that a record is flagged whatever wrote it.
/// A model without any <see cref="IHasAttention"/> type costs a save nothing.
/// </remarks>
internal sealed class AttentionSave
{
    // How many keys one query for stored signals names, so a large save stays inside the provider's parameter limits.
    private const int KeysPerQuery = 1000;

    private static readonly ConditionalWeakTable<IModel, object> modelsWithAttention = new();
    private static readonly object Yes = new();
    private static readonly object No = new();

    private readonly List<AttentionEntityOutcome> outcomes = [];
    private List<PendingIndexedSignal>? pending;

    private AttentionSave() { }

    /// <summary>Whether the model has an entity type that takes part in attention. Worked out once per model.</summary>
    public static bool AppliesTo(IModel model) => ReferenceEquals(Yes, modelsWithAttention.GetValue(model,
        static model => model.GetEntityTypes().Any(type => typeof(IHasAttention).IsAssignableFrom(type.ClrType)) ? Yes : No));

    /// <summary>
    /// Whether signals of new rows of an indexed type wait for the rows' keys. The save then writes twice, and both
    /// writes must commit together.
    /// </summary>
    public bool HasPendingRows => pending is { Count: > 0 };

    private static readonly HashSet<string> BookkeepingColumns =
    [
        nameof(IHasAttention.HasActiveAttention),
        nameof(IHasAttention.HighestSeverity),
        nameof(IHasAttention.ActiveSignalCount),
        AttentionSignalJsonHelper.ShadowPropertyName,
    ];

    /// <summary>
    /// Whether a modified row changed in nothing but attention's own columns: the summary columns and the stored
    /// signals. That is attention keeping its books, such as a host acknowledging signals by hand, and not a change of
    /// the record, so its evaluators are not asked. A rule that holds as long as the record is in some state would
    /// otherwise raise again in the very save that acknowledged it.
    /// </summary>
    public static bool IsOnlyBookkeeping(EntityEntry entry)
    {
        foreach (var property in entry.Properties)
            if (property.IsModified && !BookkeepingColumns.Contains(property.Metadata.Name))
                return false;

        return true;
    }

    /// <summary>
    /// Runs the evaluators of the rows the save is about to write, and stores what they raise in the same save.
    /// </summary>
    /// <returns>The attention work left for after the write, or null when no row raised a new signal.</returns>
    public static async ValueTask<AttentionSave?> EvaluateAsync(
        ShiftDbContext db,
        List<EntityEntry> rows,
        IServiceProvider services,
        AttentionOptions options,
        bool async,
        CancellationToken cancellationToken)
    {
        var stored = await LoadStoredSignalsAsync(db, rows, async, cancellationToken);
        AttentionSave? save = null;

        // The list is the save's own: an evaluator's signal adds a row to the context while this runs.
        foreach (var entry in rows)
        {
            IReadOnlyList<StoredAttentionSignal>? indexedSignals = null;

            if (stored is not null && entry.State == EntityState.Modified && entry.Entity is IHasIndexedAttention)
                stored.TryGetValue((entry.Entity.GetType().Name, (long)entry.Property("ID").CurrentValue!), out indexedSignals);

            var outcome = AttentionPipeline.ProcessEntity(db, entry, services, options, indexedSignals);

            if (outcome is null)
                continue;

            save ??= new();
            save.outcomes.Add(outcome);

            if (outcome.PendingIndexed is not null)
                (save.pending ??= []).AddRange(outcome.PendingIndexed);
        }

        return save;
    }

    // The stored signals of the indexed rows that already exist, with one query per entity type and not one per row,
    // so a save of many rows costs the same number of queries as a save of one.
    private static async ValueTask<Dictionary<(string EntityType, long EntityId), IReadOnlyList<StoredAttentionSignal>>?> LoadStoredSignalsAsync(
        ShiftDbContext db, List<EntityEntry> rows, bool async, CancellationToken cancellationToken)
    {
        Dictionary<string, List<long>>? keysByType = null;

        foreach (var entry in rows)
        {
            if (entry.State != EntityState.Modified || entry.Entity is not IHasIndexedAttention)
                continue;

            var id = (long)entry.Property("ID").CurrentValue!;

            if (id == 0)
                continue;

            keysByType ??= [];
            var entityType = entry.Entity.GetType().Name;

            if (!keysByType.TryGetValue(entityType, out var keys))
                keysByType[entityType] = keys = [];

            keys.Add(id);
        }

        if (keysByType is null)
            return null;

        var tracked = TrackedSignals(db);
        var stored = new Dictionary<(string, long), IReadOnlyList<StoredAttentionSignal>>();

        foreach (var (entityType, keys) in keysByType)
        {
            foreach (var chunk in keys.Chunk(KeysPerQuery))
            {
                // Read without tracking: a save of many rows would otherwise leave every signal they ever had in the
                // context.
                var signals = db.Set<AttentionSignalEntry>().AsNoTracking().Where(x => x.EntityType == entityType);
                var only = chunk[0];

                signals = chunk.Length == 1
                    ? signals.Where(x => x.EntityId == only)
                    : signals.Where(x => chunk.Contains(x.EntityId));

                signals = signals.OrderBy(x => x.ID);

                foreach (var read in async ? await signals.ToListAsync(cancellationToken) : signals.ToList())
                {
                    // A signal row the context already tracks counts as the caller left it in this unit of work, cleared
                    // by hand for example, and not as the database still holds it.
                    var signal = tracked is not null && tracked.TryGetValue(read.ID, out var inContext) ? inContext : read;
                    var key = (entityType, signal.EntityId);

                    if (!stored.TryGetValue(key, out var ofRow))
                        stored[key] = ofRow = new List<StoredAttentionSignal>();

                    ((List<StoredAttentionSignal>)ofRow).Add(signal.ToStoredSignal());
                }
            }
        }

        return stored;
    }

    // The signal rows the context tracks, by key. Usually none. Looked up without detecting changes: the save has just
    // detected them.
    private static Dictionary<long, AttentionSignalEntry>? TrackedSignals(ShiftDbContext db)
    {
        Dictionary<long, AttentionSignalEntry>? tracked = null;
        var detect = db.ChangeTracker.AutoDetectChangesEnabled;
        db.ChangeTracker.AutoDetectChangesEnabled = false;

        try
        {
            foreach (var entry in db.ChangeTracker.Entries<AttentionSignalEntry>())
                if (entry.State != EntityState.Added)
                    (tracked ??= [])[entry.Entity.ID] = entry.Entity;
        }
        finally
        {
            db.ChangeTracker.AutoDetectChangesEnabled = detect;
        }

        return tracked;
    }

    /// <summary>Adds the signals that waited for a new row's key, now that the save has written the rows.</summary>
    public void AddPendingRows(ShiftDbContext db)
    {
        if (pending is not null)
            AttentionPipeline.FlushPendingSignals(db, pending);
    }

    /// <summary>One event for each new signal, now that every row has its key.</summary>
    /// <param name="originConnectionId">
    /// The hub connection of the window that made the change, read while the request is current, so that the fan-out,
    /// which runs later in the background, can leave that window out. Null when there is none.
    /// </param>
    public List<AttentionRaised> CreateEvents(string? originConnectionId)
    {
        var events = new List<AttentionRaised>();

        foreach (var outcome in outcomes)
        {
            var entityId = (long)outcome.Entry.Property("ID").CurrentValue!;

            foreach (var signal in outcome.NewSignals)
            {
                events.Add(new AttentionRaised
                {
                    EntityType = outcome.EntityTypeName,
                    EntityId = entityId,
                    Signal = signal,
                    OriginConnectionId = originConnectionId,
                });
            }
        }

        return events;
    }

    /// <summary>
    /// Publishes the events in order. It runs once the save has committed, where nothing is left to fail, so a
    /// dispatcher that throws is logged and the next event carries on.
    /// </summary>
    public static void Publish(IAttentionDispatcher dispatcher, List<AttentionRaised> events, ILogger? logger)
        => _ = PublishAsync(dispatcher, events, logger);

    // The framework's dispatcher writes to a channel and completes at once, so this has finished when Publish returns.
    // A dispatcher that really waits carries on in the background, still one event after another.
    private static async Task PublishAsync(IAttentionDispatcher dispatcher, List<AttentionRaised> events, ILogger? logger)
    {
        foreach (var attentionRaised in events)
        {
            try
            {
                await dispatcher.PublishAsync(attentionRaised).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                logger?.LogError(exception, "Could not publish the attention signal {Category} raised on {EntityType} {EntityId}. The save isn't affected.",
                    attentionRaised.Signal.Category, attentionRaised.EntityType, attentionRaised.EntityId);
            }
        }
    }
}
