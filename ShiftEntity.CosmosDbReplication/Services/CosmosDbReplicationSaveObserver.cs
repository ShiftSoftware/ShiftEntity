using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.Model.Enums;
using ShiftSoftware.ShiftEntity.Model.Replication;

namespace ShiftSoftware.ShiftEntity.CosmosDbReplication.Services;

/// <summary>
/// Replicates what a save wrote, through the host's <c>SetUpReplication</c> registrations. It replaced the after-save
/// trigger of the trigger library, and it keeps that trigger's order: inside the save it runs the row's preparer, in
/// the caller's scope; the sync itself starts once the save is committed, in a background task with a scope of its
/// own, and records its result by key. A root of a replication tree that the save changed through another row of its
/// tree gets a sync of its tree document too, after the commit.
/// </summary>
/// <remarks>
/// <para>
/// Only the start of the sync waits for the commit. A preparer queries the caller's context, and that context cannot
/// be used while EF reports the commit, so preparers still run inside the save, as they did in the trigger. A row
/// whose type has no registration starts nothing.
/// </para>
/// <para>
/// The saves one context makes in one transaction start their syncs together at its commit, so one row's syncs are
/// combined there and the document matches the row as committed: a row added and deleted again in the transaction
/// starts nothing, a row deleted and added again with the same key is deleted and then written, in that order, and
/// otherwise one sync writes the row's last state. A save that is undone, by its own failure or by a rollback to a
/// savepoint, takes its syncs with it. Without a transaction each save's syncs start when it ends, as before.
/// </para>
/// <para>
/// In a transaction the context does not follow (a <c>TransactionScope</c>, or one it joined with
/// <c>UseTransaction</c>) the syncs could only start before the commit: a tree sync would then read the tree from its
/// own connection as it was before the change, and its watermark would clear the mark the save made. So the context
/// refuses such a save before it writes, when it has rows this observer replicates (see <see cref="WaitsFor"/>). Under
/// <c>SuppressReplication</c> the save goes through, and the catch-up replicates its rows once the transaction has
/// committed.
/// </para>
/// </remarks>
internal sealed class CosmosDbReplicationSaveObserver(IServiceProvider services) : IShiftDbContextSaveObserver, IWaitsForCommitSaveObserver
{
    /// <summary>
    /// At most this many syncs that load a replication tree run at once for one save, or for one transaction's commit.
    /// Each loads its root with the tree, reads the container and writes the document and the watermark, so a part
    /// shared by many roots would otherwise take that many connections at once. The bound is per save or commit, not
    /// per process: the syncs of saves and commits that run at the same time do not count against each other.
    /// </summary>
    internal const int MaxConcurrentTreeSyncs = 8;

    private delegate ValueTask<Sync?> PrepareRow(object entity, SavedRowChange change, IServiceProvider callerServices,
        IServiceProvider registrations);

    private delegate Sync? PrepareRoot(long id, IServiceProvider registrations);

    private delegate bool IsRegistered(IServiceProvider registrations, bool withTreeDocument);

    // One starter of each kind per entity type, or null for a type that cannot be registered for replication.
    private static readonly ConcurrentDictionary<Type, (PrepareRow Row, PrepareRoot Root, IsRegistered Registered)?> starters = new();

    public string Work => "replication";

    public string Switch => "SuppressReplication()";

    // Only the rows of the types that can be replicated. Whether a type has a registration is asked when a row of it
    // was saved.
    public bool Wants(ShiftDbContext context, Type entityType) =>
        typeof(IShiftEntityReplication).IsAssignableFrom(entityType) && StartersFor(entityType) is not null;

    public bool WaitsFor(ShiftDbContext context, Type entityType, bool asChangedRoot)
    {
        if (StartersFor(entityType) is not { } start || services.GetService<ShiftEntityCosmosDbOptions>() is not { } options)
            return false;

        using var registrations = options.internalServices.BuildServiceProvider();

        // A root that changed through another row of its tree is replicated only as a tree document.
        return start.Registered(registrations, asChangedRoot);
    }

    public async ValueTask SavedAsync(ShiftDbContext context, SaveReport report, CancellationToken cancellationToken)
    {
        ShiftEntityCosmosDbOptions? options = null;
        ServiceProvider? registrations = null;
        List<Sync>? syncs = null;

        try
        {
            foreach (var row in report.Rows)
            {
                if (row.Entity is not IShiftEntityReplication || StartersFor(row.EntityType) is not { } start)
                    continue;

                options ??= services.GetService<ShiftEntityCosmosDbOptions>();
                if (options is null)
                    return;

                registrations ??= options.internalServices.BuildServiceProvider();
                if (await start.Row(row.Entity, row.Change, services, registrations) is { } sync)
                    (syncs ??= new()).Add(sync);
            }

            foreach (var (root, ids) in report.ChangedRoots)
            {
                if (StartersFor(root) is not { } start)
                    continue;

                options ??= services.GetService<ShiftEntityCosmosDbOptions>();
                if (options is null)
                    return;

                registrations ??= options.internalServices.BuildServiceProvider();
                foreach (var id in ids)
                    if (start.Root(id, registrations) is { } sync)
                        (syncs ??= new()).Add(sync);
            }
        }
        finally
        {
            registrations?.Dispose();
        }

        if (syncs is null)
            return;

        var scopes = services.GetRequiredService<IServiceScopeFactory>();
        var logger = services.GetRequiredService<ILogger<CosmosDbReplicationSaveObserver>>();

        // The syncs of this save wait for the commit in the save's unit. The saves that commit together start theirs
        // as one batch, where the first of them queued it, and a save that is undone drops out of it.
        context.AfterCommitTogether(typeof(CosmosDbReplicationSaveObserver), syncs,
            saves => new Batch(scopes, logger, saves.SelectMany(save => (List<Sync>)save).ToList()).Start());
    }

    private static (PrepareRow Row, PrepareRoot Root, IsRegistered Registered)? StartersFor(Type entityType) => starters.GetOrAdd(entityType, type =>
    {
        // The registrations are generic over ShiftEntity<T> of the replicated type itself, as the trigger was. A row of
        // a type derived from it (mapped to its table) replicates through the base type's registration, as the
        // trigger, raised for every type the row is, did too.
        for (var registered = type; registered is not null && registered != typeof(object); registered = registered.BaseType)
        {
            if (!typeof(IShiftEntityReplication).IsAssignableFrom(registered) ||
                !typeof(ShiftEntity<>).MakeGenericType(registered).IsAssignableFrom(registered))
                continue;

            MethodInfo Method(string name) =>
                typeof(CosmosDbReplicationSaveObserver).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(registered);

            return (Method(nameof(PrepareRowAsync)).CreateDelegate<PrepareRow>(), Method(nameof(PrepareTreeSync)).CreateDelegate<PrepareRoot>(),
                Method(nameof(HasRegistration)).CreateDelegate<IsRegistered>());
        }

        return null;
    });

    private static bool HasRegistration<EntityType>(IServiceProvider registrations, bool withTreeDocument)
        where EntityType : ShiftEntity<EntityType>, IShiftEntityReplication =>
        registrations.GetService<CosmosDbOnSaveReferenceOperations<EntityType>>() is { } operations && (!withTreeDocument || operations.HasTreeDocument);

    private static async ValueTask<Sync?> PrepareRowAsync<EntityType>(object row, SavedRowChange change,
        IServiceProvider callerServices, IServiceProvider registrations)
        where EntityType : ShiftEntity<EntityType>, IShiftEntityReplication
    {
        var operations = registrations.GetService<CosmosDbOnSaveReferenceOperations<EntityType>>();
        if (operations is null)
            return null;

        var kind = change switch
        {
            SavedRowChange.Added => SyncKind.Added,
            SavedRowChange.Deleted => SyncKind.Deleted,
            _ => SyncKind.Modified,
        };

        var entity = (EntityType)row;
        if (callerServices.GetService<IShiftEntityPrepareForReplicationAsync<EntityType>>() is { } preparer)
            entity = await preparer.PrepareForReplicationAsync(entity, ChangeType(kind));

        return new Sync(typeof(EntityType), entity.ID, kind, entity.GetType(), operations.HasTreeDocument,
            (scope, runAs) => operations.RunAsync(entity, scope, ChangeType(runAs)));
    }

    private static Sync? PrepareTreeSync<EntityType>(long id, IServiceProvider registrations)
        where EntityType : ShiftEntity<EntityType>, IShiftEntityReplication
    {
        var operations = registrations.GetService<CosmosDbOnSaveReferenceOperations<EntityType>>();
        if (operations is null || !operations.HasTreeDocument)
            return null;

        return new Sync(typeof(EntityType), id, SyncKind.Tree, typeof(EntityType), true, (scope, _) => operations.RunTreeAsync(id, scope));
    }

    private static ReplicationChangeType ChangeType(SyncKind kind) => kind switch
    {
        SyncKind.Added => ReplicationChangeType.Added,
        SyncKind.Deleted => ReplicationChangeType.Deleted,
        _ => ReplicationChangeType.Modified,
    };

    private enum SyncKind { Added, Modified, Deleted, Tree }

    // One sync of one row: of its own save (Added, Modified, Deleted) or of its tree after a part changed (Tree). Run
    // gets the kind to run as, because combining can make a new row's later edit run as its insert.
    private sealed record Sync(Type Registration, long Id, SyncKind Kind, Type LoggedType, bool LoadsTree,
        Func<IServiceProvider, SyncKind, Task> Run);

    // The syncs that start together: one save's, or one transaction's at its commit.
    private sealed class Batch(IServiceScopeFactory scopes, ILogger logger, List<Sync> syncs)
    {
        public void Start()
        {
            var plans = new Dictionary<(Type, long), List<Sync>>();
            var order = new List<List<Sync>>();
            foreach (var sync in syncs)
            {
                if (!plans.TryGetValue((sync.Registration, sync.Id), out var plan))
                {
                    plans[(sync.Registration, sync.Id)] = plan = new();
                    order.Add(plan);
                }
                Combine(plan, sync);
            }

            var trees = new SemaphoreSlim(MaxConcurrentTreeSyncs);
            foreach (var plan in order)
                if (plan.Count > 0)
                    Run(plan, plan[0].LoadsTree ? trees : null);
        }

        // One row's syncs, in the order the saves made them, reduced to what the committed row needs.
        private static void Combine(List<Sync> plan, Sync next)
        {
            var last = plan.Count > 0 ? plan[^1] : null;
            switch (next.Kind)
            {
                case SyncKind.Tree:
                    // A sync of the row's own save rebuilds its tree too, and a deleted root has none.
                    if (last is null)
                        plan.Add(next);
                    break;

                case SyncKind.Added:
                    // After a delete, the same key added again: the delete runs first.
                    if (last is null || last.Kind == SyncKind.Deleted)
                        plan.Add(next);
                    else
                        plan[^1] = next;
                    break;

                case SyncKind.Modified:
                    if (last is null)
                        plan.Add(next);
                    else if (last.Kind == SyncKind.Added)
                        plan[^1] = next with { Kind = SyncKind.Added };   // still a new row
                    else if (last.Kind != SyncKind.Deleted)
                        plan[^1] = next;
                    break;

                case SyncKind.Deleted:
                    // A row added in the transaction has no document yet, so nothing is left to do for it.
                    if (last is null)
                        plan.Add(next);
                    else if (last.Kind == SyncKind.Added)
                        plan.RemoveAt(plan.Count - 1);
                    else if (last.Kind != SyncKind.Deleted)
                        plan[^1] = next;
                    break;
            }
        }

        // One row's syncs, one after another, in a background task with a scope of its own each, with the messages the
        // trigger logged.
        private void Run(List<Sync> plan, SemaphoreSlim? gate)
        {
            foreach (var sync in plan)
                logger.LogInformation("CosmosDB Syncing is starting to Sync {entityType} - With ID: {entityID}", sync.LoggedType, sync.Id);

            _ = Task.Run(async () =>
            {
                if (gate is not null)
                    await gate.WaitAsync();

                try
                {
                    foreach (var sync in plan)
                    {
                        var scope = scopes.CreateAsyncScope();
                        try
                        {
                            logger.LogInformation("CosmosDB Syncing Task is Running {entityType} - With ID: {entityID}", sync.LoggedType, sync.Id);
                            await sync.Run(scope.ServiceProvider, sync.Kind);
                            logger.LogInformation("CosmosDB Syncing Succeeded for {entityType} - With ID: {entityID}", sync.LoggedType, sync.Id);
                        }
                        catch (Exception ex)
                        {
                            logger.LogError("CosmosDB Syncing Failed for {entityType} - With ID: {entityID} with Exception: {exception}",
                                sync.LoggedType, sync.Id, new AggregateException(ex));
                        }
                        finally
                        {
                            await scope.DisposeAsync();
                        }
                    }
                }
                finally
                {
                    gate?.Release();
                }
            });
        }
    }
}
