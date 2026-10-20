using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.CosmosDbReplication.Exceptions;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.EFCore.Entities;
using ShiftSoftware.ShiftEntity.EFCore.Replication;
using ShiftSoftware.ShiftEntity.Model.Enums;
using ShiftSoftware.ShiftEntity.Model.Replication;
using System.Linq.Expressions;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace ShiftSoftware.ShiftEntity.CosmosDbReplication;

public class ShiftEntityCosmosDbOptions
{
    internal readonly IServiceCollection internalServices = new ServiceCollection();
    public IServiceProvider Services { get; private set; }

    public ShiftEntityCosmosDbOptions(IServiceProvider serviceProvider)
    {
        this.Services = serviceProvider;
    }

    public CosmosDbTriggerReplicateOperation<Entity> SetUpReplication<DB, Entity>(string cosmosDbConnectionString, string cosmosDataBaseId,
        Func<EntityWrapper<Entity>, ValueTask<Entity>>? mapper = null)
        where Entity : ShiftEntity<Entity>, IShiftEntityReplication
        where DB : ShiftDbContext
    {
        return new(cosmosDbConnectionString, cosmosDataBaseId, mapper, this.internalServices, typeof(DB));
    }

    public CosmosDbTriggerReplicateOperation<Entity> SetUpReplication<DB, Entity>(CosmosClient client, string cosmosDataBaseId,
        Func<EntityWrapper<Entity>, ValueTask<Entity>>? mapper = null)
        where Entity : ShiftEntity<Entity>, IShiftEntityReplication
        where DB : ShiftDbContext
    {
        return new(client, cosmosDataBaseId, mapper, this.internalServices, typeof(DB));
    }
}

public class CosmosDbTriggerReplicateOperation<Entity>
    where Entity : ShiftEntity<Entity>, IShiftEntityReplication
{
    private readonly CosmosDbTriggerReferenceOperations<Entity> cosmosDbTriggerReferenceOperations;

    public CosmosDbTriggerReplicateOperation(string cosmosDbConnectionString, string cosmosDataBaseId,
        Func<EntityWrapper<Entity>, ValueTask<Entity>>? setupMapping, IServiceCollection services, Type dbContextType)
    {
        this.cosmosDbTriggerReferenceOperations =
            new CosmosDbTriggerReferenceOperations<Entity>(cosmosDbConnectionString, cosmosDataBaseId, setupMapping,
            dbContextType);
        services.AddScoped(x => this.cosmosDbTriggerReferenceOperations);
    }

    public CosmosDbTriggerReplicateOperation(CosmosClient client, string cosmosDataBaseId,
        Func<EntityWrapper<Entity>, ValueTask<Entity>>? setupMapping, IServiceCollection services, Type dbContextType)
    {
        this.cosmosDbTriggerReferenceOperations =
            new CosmosDbTriggerReferenceOperations<Entity>(client, cosmosDataBaseId, setupMapping,
            dbContextType);
        services.AddScoped(x => this.cosmosDbTriggerReferenceOperations);
    }

    /// <summary>
    ///
    /// </summary>
    /// <typeparam name="CosmosDbItem"></typeparam>
    /// <param name="cosmosContainerId"></param>
    /// <param name="mapping">
    /// Builds the Cosmos document from the entity. Optional: when omitted, the document is mapped through the host's
    /// registered ShiftMapper mapper (<see cref="ShiftMapper.IMapper"/>), which must declare
    /// <c>CreateMap&lt;Entity, CosmosDbItem&gt;()</c>. No registered mapper, or none declaring the pair, fails the
    /// sync loudly rather than leaving the row dirty.
    /// </param>
    /// <returns></returns>
    public CosmosDbTriggerReferenceOperations<Entity> Replicate<CosmosDbItem>(string cosmosContainerId,
        Expression<Func<CosmosDbItem, object>> partitionKeyLevel1Expression,
        Func<EntityWrapper<Entity>, CosmosDbItem>? mapping = null)
    {
        this.cosmosDbTriggerReferenceOperations
            .Replicate(cosmosContainerId, partitionKeyLevel1Expression, null, null, mapping);
        return this.cosmosDbTriggerReferenceOperations;
    }

    /// <summary>
    ///
    /// </summary>
    /// <typeparam name="CosmosDbItem"></typeparam>
    /// <param name="cosmosContainerId"></param>
    /// <param name="mapping">
    /// Builds the Cosmos document from the entity. Optional: when omitted, the document is mapped through the host's
    /// registered ShiftMapper mapper (<see cref="ShiftMapper.IMapper"/>), which must declare
    /// <c>CreateMap&lt;Entity, CosmosDbItem&gt;()</c>. No registered mapper, or none declaring the pair, fails the
    /// sync loudly rather than leaving the row dirty.
    /// </param>
    /// <returns></returns>
    public CosmosDbTriggerReferenceOperations<Entity> Replicate<CosmosDbItem>(
        string cosmosContainerId,
        Expression<Func<CosmosDbItem, object>> partitionKeyLevel1Expression,
        Expression<Func<CosmosDbItem, object>> partitionKeyLevel2Expression,
        Func<EntityWrapper<Entity>, CosmosDbItem>? mapping = null)
    {
        this.cosmosDbTriggerReferenceOperations.Replicate(cosmosContainerId, partitionKeyLevel1Expression,
            partitionKeyLevel2Expression, null, mapping);
        return this.cosmosDbTriggerReferenceOperations;
    }

    /// <summary>
    ///
    /// </summary>
    /// <typeparam name="CosmosDbItem"></typeparam>
    /// <param name="cosmosContainerId"></param>
    /// <param name="mapping">
    /// Builds the Cosmos document from the entity. Optional: when omitted, the document is mapped through the host's
    /// registered ShiftMapper mapper (<see cref="ShiftMapper.IMapper"/>), which must declare
    /// <c>CreateMap&lt;Entity, CosmosDbItem&gt;()</c>. No registered mapper, or none declaring the pair, fails the
    /// sync loudly rather than leaving the row dirty.
    /// </param>
    /// <returns></returns>
    public CosmosDbTriggerReferenceOperations<Entity> Replicate<CosmosDbItem>(
        string cosmosContainerId,
        Expression<Func<CosmosDbItem, object>> partitionKeyLevel1Expression,
        Expression<Func<CosmosDbItem, object>> partitionKeyLevel2Expression,
        Expression<Func<CosmosDbItem, object>> partitionKeyLevel3Expression,
        Func<EntityWrapper<Entity>, CosmosDbItem>? mapping = null)
    {
        this.cosmosDbTriggerReferenceOperations.Replicate(cosmosContainerId, partitionKeyLevel1Expression,
            partitionKeyLevel2Expression, partitionKeyLevel3Expression, mapping);
        return this.cosmosDbTriggerReferenceOperations;
    }

    /// <summary>
    /// Replicates <typeparamref name="CosmosDbItem"/>, built from the root loaded by ID with its replication tree
    /// (<c>HasReplicationTree</c>) once the save is committed, instead of from the saving caller's instance. A save of
    /// the root or of any row in its tree replicates it, and the row's stamp locates the document, as for
    /// <c>Replicate</c>.
    /// </summary>
    /// <remarks>
    /// The tree document is the registration's only document: a registration replicates either its own document
    /// (<c>Replicate</c>) or its tree, and updates no references. A save of a part records the root's watermark after
    /// rebuilding this one document, so that watermark could not stand for anything else.
    /// </remarks>
    /// <param name="mapping">
    /// Builds the document from the loaded root. Optional: when omitted, the document is mapped through the host's
    /// registered ShiftMapper mapper, which must declare <c>CreateMap&lt;Entity, CosmosDbItem&gt;()</c>.
    /// </param>
    public CosmosDbTriggerReferenceOperations<Entity> ReplicateTree<CosmosDbItem>(string cosmosContainerId,
        Expression<Func<CosmosDbItem, object>> partitionKeyLevel1Expression,
        Func<EntityWrapper<Entity>, CosmosDbItem>? mapping = null)
    {
        this.cosmosDbTriggerReferenceOperations.ReplicateTree(cosmosContainerId, partitionKeyLevel1Expression, null, null, mapping);
        return this.cosmosDbTriggerReferenceOperations;
    }

    /// <inheritdoc cref="ReplicateTree{CosmosDbItem}(string, Expression{Func{CosmosDbItem, object}}, Func{EntityWrapper{Entity}, CosmosDbItem}?)"/>
    public CosmosDbTriggerReferenceOperations<Entity> ReplicateTree<CosmosDbItem>(string cosmosContainerId,
        Expression<Func<CosmosDbItem, object>> partitionKeyLevel1Expression,
        Expression<Func<CosmosDbItem, object>> partitionKeyLevel2Expression,
        Func<EntityWrapper<Entity>, CosmosDbItem>? mapping = null)
    {
        this.cosmosDbTriggerReferenceOperations.ReplicateTree(cosmosContainerId, partitionKeyLevel1Expression, partitionKeyLevel2Expression, null, mapping);
        return this.cosmosDbTriggerReferenceOperations;
    }
}

public class CosmosDbTriggerReferenceOperations<Entity>
    where Entity : ShiftEntity<Entity>, IShiftEntityReplication
{
    private readonly string cosmosDbConnectionString;
    private readonly string cosmosDataBaseId;
    private readonly Func<EntityWrapper<Entity>, ValueTask<Entity>>? setupMapping;
    internal readonly Type dbContextType;
    private CosmosClient? client;
    private List<string> cosmosContainerIds = new();

    //Each step of a sync gives why it failed when it did not succeed, so that RunAsync can report the failure.
    private Func<Entity, IServiceProvider, Database, Task<(bool success, string? stamp, string? failure)>>? replicateAction;
    //Hard-delete companion of the registration's document, whichever builds it: removes the document the row's stamp
    //locates. Save-time only (the timer/catch-up sync never sees a hard delete). Set by Replicate and ReplicateTree.
    private Func<Entity, IServiceProvider, Database, Task<bool>>? deleteAction;
    private List<Func<Entity, IServiceProvider, Database, Task<(bool success, string? failure)>>> upsertReferenceActions = new();

    //The document built from the root loaded by ID with its replication tree (ReplicateTree). Null when the root cannot
    //be loaded. A registration has this or replicateAction, never both.
    private Func<long, IServiceProvider, Database, Task<TreeSync?>>? treeAction;

    private sealed record TreeSync(bool Success, string? Stamp, DateTimeOffset Version, string? Failure = null);

    /// <summary>Whether this registration builds a document from the root's replication tree.</summary>
    internal bool HasTreeDocument => treeAction is not null;

    //The stamp this process last wrote for each instance it synced. A later sync of the same instance uses it instead
    //of the instance's own column, which RunAsync does not change: the instance is often the caller's, still tracked
    //by the caller's context, and a changed column would make the caller's next save write the row again. An entry is
    //kept only as long as its instance is alive.
    private static readonly ConditionalWeakTable<Entity, string> writtenStamps = new();

    private static string? LastWrittenStamp(Entity entity) =>
        writtenStamps.TryGetValue(entity, out var stamp) ? stamp : entity.LastReplicationStamp;

    internal CosmosDbTriggerReferenceOperations(string cosmosDbConnectionString, string cosmosDataBaseId,
        Func<EntityWrapper<Entity>, ValueTask<Entity>>? setupMapping, Type dbContextType)
    {
        this.cosmosDbConnectionString = cosmosDbConnectionString;
        this.cosmosDataBaseId = cosmosDataBaseId;
        this.setupMapping = setupMapping;
        this.dbContextType = dbContextType;
    }

    internal CosmosDbTriggerReferenceOperations(CosmosClient client, string cosmosDataBaseId,
        Func<EntityWrapper<Entity>, ValueTask<Entity>>? setupMapping, Type dbContextType)
    {
        this.client = client;
        this.cosmosDataBaseId = cosmosDataBaseId;
        this.setupMapping = setupMapping;
        this.dbContextType = dbContextType;
    }

    internal void Replicate<CosmosDbItem>(
        string cosmosContainerId,
        Expression<Func<CosmosDbItem, object>> partitionKeyLevel1Expression,
        Expression<Func<CosmosDbItem, object>>? partitionKeyLevel2Expression,
        Expression<Func<CosmosDbItem, object>>? partitionKeyLevel3Expression,
        Func<EntityWrapper<Entity>, CosmosDbItem>? mapping)
    {
        if (this.treeAction is not null)
            throw OneDocumentOnly();

        this.cosmosContainerIds.Add(cosmosContainerId);

        //The partition-key expressions document the container's key shape and are validated up front; the actual
        //key values always come from the mapped item itself (the document JSON defines the partition key).
        if (partitionKeyLevel1Expression is not null)
            CheckPartitionKeyType(partitionKeyLevel1Expression);
        if (partitionKeyLevel2Expression is not null)
            CheckPartitionKeyType(partitionKeyLevel2Expression);
        if (partitionKeyLevel3Expression is not null)
            CheckPartitionKeyType(partitionKeyLevel3Expression);

        this.replicateAction = async (entity, services, db) =>
        {
            //No delegate → the host's ShiftMapper mapper, resolved and pair-checked BEFORE anything is written. A
            //missing registration then throws out of the run (the save-time sync logs it and the row stays dirty) instead
            //of surfacing as a permanently-dirty document under a clean-looking watermark, which is what the old
            //AutoMapper fallback did when the host had forgotten to register it.
            CosmosDbItem item = mapping is not null
                ? mapping(new EntityWrapper<Entity>(entity, services))
                : ReplicationMapper.ResolveCreate<Entity, CosmosDbItem>(services,
                    $"Replicate<{typeof(CosmosDbItem).Name}>(\"{cosmosContainerId}\")")(entity);

            var container = db.GetContainer(cosmosContainerId);

            //Detect an id or partition-key change since the last successful sync and remove the stale Cosmos
            //document under the OLD id + key before upserting under the new one (Cosmos can't mutate a document's
            //id or PK, so a naive upsert would orphan the old doc). The OLD coordinates come from the stamp of the
            //last write (LastWrittenStamp: this process's record for the instance, else its persisted
            //LastReplicationStamp) — what was actually written to Cosmos last time — never from re-mapping the
            //pre-save entity snapshot: a mapping that leaves a partition-key component unset (or derives values
            //from navigations) reconstructs coordinates that don't match the stored document, so such a delete
            //silently misses (404) and the old document is orphaned. A stamp written for another container
            //locates no document in this one (see CanBeIn), so nothing is deleted for it.
            string? staleId = null;
            PartitionKey? stalePartitionKey = null;

            var containerResponse = await container.ReadContainerAsync();
            var newStamp = Utility.BuildStamp(containerResponse, item!);
            var oldStamp = LastReplicationStamp.Deserialize(LastWrittenStamp(entity));

            if (oldStamp is not null && newStamp.DiffersFrom(oldStamp) && oldStamp.CanBeIn(containerResponse))
            {
                staleId = oldStamp.Id;
                stalePartitionKey = oldStamp.BuildPartitionKey();
            }

            string stamp = newStamp.Serialize();

            if (staleId is not null && stalePartitionKey.HasValue)
            {
                try
                {
                    await container.DeleteItemAsync<CosmosDbItem>(staleId, stalePartitionKey.Value);
                }
                catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                {
                    //already gone; treat as success and proceed to upsert
                }
                catch (Exception ex)
                {
                    //Report failure WITHOUT upserting: the stamp/replication date stay untouched, the row stays
                    //dirty, and the catch-up sync retries the delete + upsert later.
                    return (false, null, StaleDeleteFailed(staleId, cosmosContainerId, ex));
                }
            }

            var response = await container.UpsertItemAsync(item);

            bool success = response.StatusCode == System.Net.HttpStatusCode.OK ||
                    response.StatusCode == System.Net.HttpStatusCode.Created ||
                    response.StatusCode == System.Net.HttpStatusCode.NoContent;

            //On success, RunAsync writes the new stamp (the id + partition key this row now lives under in
            //Cosmos) to the row and keeps it for this instance, so the NEXT sync — save-time or catch-up — can
            //detect the next change.
            return (success, success ? stamp : null, success ? null : UpsertFailed(cosmosContainerId, response.StatusCode));
        };

        this.deleteAction = DeleteByStamp<CosmosDbItem>(cosmosContainerId);
    }

    internal void ReplicateTree<CosmosDbItem>(
        string cosmosContainerId,
        Expression<Func<CosmosDbItem, object>> partitionKeyLevel1Expression,
        Expression<Func<CosmosDbItem, object>>? partitionKeyLevel2Expression,
        Expression<Func<CosmosDbItem, object>>? partitionKeyLevel3Expression,
        Func<EntityWrapper<Entity>, CosmosDbItem>? mapping)
    {
        if (this.replicateAction is not null)
            throw OneDocumentOnly();
        if (this.upsertReferenceActions.Count > 0)
            throw NoReferencesWithTree();

        this.cosmosContainerIds.Add(cosmosContainerId);

        CheckPartitionKeyType(partitionKeyLevel1Expression);
        if (partitionKeyLevel2Expression is not null)
            CheckPartitionKeyType(partitionKeyLevel2Expression);
        if (partitionKeyLevel3Expression is not null)
            CheckPartitionKeyType(partitionKeyLevel3Expression);

        this.treeAction = async (id, services, db) =>
        {
            var map = mapping is not null
                ? (Func<Entity, CosmosDbItem>)(root => mapping(new EntityWrapper<Entity>(root, services)))
                : ReplicationMapper.ResolveCreate<Entity, CosmosDbItem>(services,
                    $"ReplicateTree<{typeof(CosmosDbItem).Name}>(\"{cosmosContainerId}\")");

            var context = (ShiftDbContext)services.GetRequiredService(this.dbContextType);
            var root = await context.Set<Entity>().IgnoreQueryFilters().AsNoTracking()
                .IncludeReplicationTree(context)
                .SingleOrDefaultAsync(x => x.ID == id);

            //Not found: deleted after the save that started this sync, whose own sync removes the document, or not
            //visible to this connection yet. Nothing is recorded, so a row that does exist stays due for the catch-up.
            if (root is null)
                return null;

            var item = map(root);
            var container = db.GetContainer(cosmosContainerId);
            var containerResponse = await container.ReadContainerAsync();
            var newStamp = Utility.BuildStamp(containerResponse, item!);

            //The document moved: remove the one the last sync wrote, as Replicate does. A stamp written for another
            //container locates no document in this one (see CanBeIn), so nothing is deleted for it.
            if (LastReplicationStamp.Deserialize(root.LastReplicationStamp) is { } oldStamp && newStamp.DiffersFrom(oldStamp) &&
                oldStamp.CanBeIn(containerResponse))
            {
                try
                {
                    await container.DeleteItemAsync<CosmosDbItem>(oldStamp.Id, oldStamp.BuildPartitionKey());
                }
                catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                {
                }
                catch (Exception ex)
                {
                    return new TreeSync(false, null, root.LastSaveDate, StaleDeleteFailed(oldStamp.Id, cosmosContainerId, ex));
                }
            }

            var response = await container.UpsertItemAsync(item);
            var success = response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created or HttpStatusCode.NoContent;

            //The version this sync replicates is the root's LastSaveDate as loaded, which a change of a part never
            //moves. See RunTreeAsync for what that means for the watermark.
            return success
                ? new TreeSync(true, newStamp.Serialize(), root.LastSaveDate)
                : new TreeSync(false, null, root.LastSaveDate, UpsertFailed(cosmosContainerId, response.StatusCode));
        };

        this.deleteAction = DeleteByStamp<CosmosDbItem>(cosmosContainerId);
    }

    //Locates the row's Cosmos document by the stamp of its last write (the id + partition key it was last written
    //under, see LastWrittenStamp) and removes it. No stamp ⇒ it was never replicated ⇒ nothing to delete. A stamp
    //written for another container (see CanBeIn) ⇒ its document is not in this one ⇒ nothing to delete either.
    private static Func<Entity, IServiceProvider, Database, Task<bool>> DeleteByStamp<CosmosDbItem>(string cosmosContainerId) =>
        async (entity, services, db) =>
        {
            var stamp = LastReplicationStamp.Deserialize(LastWrittenStamp(entity));
            if (stamp is null)
                return true;

            var container = db.GetContainer(cosmosContainerId);
            try
            {
                if (stamp.CanBeIn(await container.ReadContainerAsync()))
                    await container.DeleteItemAsync<CosmosDbItem>(stamp.Id, stamp.BuildPartitionKey());
            }
            catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                //Already gone, or no such container; treat as success.
            }
            return true;
        };

    //Why a step of a sync failed, for the sync's failure.
    private static string StaleDeleteFailed(string? id, string containerId, Exception exception) =>
        $"the document the last sync wrote ('{id}' in '{containerId}') could not be deleted, so the new one was not written: " +
        Utility.DescribeFailure(exception);

    private static string UpsertFailed(string containerId, HttpStatusCode status) =>
        $"Cosmos DB answered {(int)status} {status} to the upsert into '{containerId}'";

    private static string ReferenceFailed(string containerId, Exception? exception) =>
        $"a document in '{containerId}' that refers to the row could not be updated: {Utility.DescribeFailure(exception)}";

    //A sync whose step reported failure, rather than throwing, used to be logged as succeeded.
    private static InvalidOperationException SyncFailed(long id, string? failure) => new(
        $"The replication of '{typeof(Entity).Name}' {id} failed: {failure ?? "a step reported failure"}. Nothing was " +
        "recorded, so the row stays due for the catch-up.");

    //One document a row: the row's own stamp, watermark and hard delete all describe that one document.
    private static ReplicationRegistrationException OneDocumentOnly() => new(
        $"The replication of '{typeof(Entity).Name}' registers both Replicate and ReplicateTree. A registration replicates one " +
        "document a row: either its own (Replicate) or the one built from its replication tree (ReplicateTree), not both.");

    //A part's change rebuilds only the tree document and then records the root's watermark, which would also say that
    //the root's reference updates had succeeded.
    private static ReplicationRegistrationException NoReferencesWithTree() => new(
        $"The replication of '{typeof(Entity).Name}' combines ReplicateTree with UpdateReference or UpdatePropertyReference. A " +
        "registration that replicates its tree updates no references: the sync of a part's change records the root as replicated " +
        "after rebuilding the tree document only.");

    private void CheckPartitionKeyType<CosmosDbItem>(Expression<Func<CosmosDbItem, object>> partitionKeyExpression)
    {
        var type = partitionKeyExpression.Body.Type;
        string? propertyName = null;

        // Check if the body of the expression is a UnaryExpression
        if (partitionKeyExpression.Body is UnaryExpression unaryExpression)
        {
            // If it is, use the Operand property to get the underlying expression
            type = unaryExpression.Operand.Type;
        }

        if (partitionKeyExpression.Body is MemberExpression memberExpression)
            propertyName = memberExpression.Member.Name;
        else if (partitionKeyExpression.Body is UnaryExpression unaryExpression2 &&
                unaryExpression2.Operand is MemberExpression memberExpression2)
            propertyName = memberExpression2.Member.Name;

        //Check if the type of the partition key is not valid throw exception
        if (!(type == typeof(bool?) || type == typeof(bool) || type == typeof(string) || type.IsNumericType()))
            throw new WrongPartitionKeyTypeException($"Partition key type of '{propertyName}' is invalid, " +
                               "Only boolean or number or string partition key types allowed");
    }

    /// <param name="mapping">
    /// Merges the entity ONTO the stored document and returns what to write. Optional: when omitted, the host's
    /// registered ShiftMapper mapper (<see cref="ShiftMapper.IMapper"/>) copies the entity onto the stored
    /// document through its <c>CreateMap&lt;Entity, CosmosDbItem&gt;()</c>; a member that map ignores — typically
    /// the partition key — survives the merge.
    /// </param>
    public CosmosDbTriggerReferenceOperations<Entity> UpdateReference<CosmosDbItem>(string cosmosContainerId,
                Func<IQueryable<CosmosDbItem>, EntityWrapper<Entity>, IQueryable<CosmosDbItem>> finder,
                Func<EntityWrapper<Entity>, CosmosDbItem, CosmosDbItem>? mapping = null)
    {
        if (this.treeAction is not null)
            throw NoReferencesWithTree();

        this.cosmosContainerIds.Add(cosmosContainerId);

        this.upsertReferenceActions.Add(async (entity, services, db) =>
        {
            //Resolved above the loop and before the lookup, so a host with no usable mapper fails on the first
            //run — whether or not any document currently references this entity.
            var merge = mapping is null
                ? ReplicationMapper.ResolveMerge<Entity, CosmosDbItem>(services,
                    $"UpdateReference<{typeof(CosmosDbItem).Name}>(\"{cosmosContainerId}\")")
                : null;

            bool? isSucceeded = null;
            string? failure = null;
            var container = db.GetContainer(cosmosContainerId);

            var items = await GetItemsForReferenceUpdate(container, entity, services, finder);

            if(items is null || items?.Count() == 0)
                return (true, null);

            List<Task> cosmosTasks = new();

            foreach (var item in items)
            {
                // Merge-onto-existing: the stored document is handed in and what comes back is what gets written,
                // so the delegate (or the mapper's update overload) decides which members survive. That is not
                // expressible as a plain entity->document map, which is why this overload exists.
                CosmosDbItem tempItems = mapping is not null
                    ? mapping(new EntityWrapper<Entity>(entity, services), item)
                    : merge!(entity, item);

                cosmosTasks.Add(container.UpsertItemAsync<CosmosDbItem>(tempItems)
                    .ContinueWith(x =>
                    {
                        isSucceeded = isSucceeded.GetValueOrDefault(true) && x.IsCompletedSuccessfully;
                        if (!x.IsCompletedSuccessfully)
                            Interlocked.CompareExchange(ref failure, ReferenceFailed(cosmosContainerId, x.Exception), null);
                    }));
            }

            await Task.WhenAll(cosmosTasks);

            return (isSucceeded.GetValueOrDefault(false), failure);
        });

        return this;
    }

    /// <param name="mapping">
    /// Builds the embedded reference document from the entity. Optional: when omitted, it is mapped through the
    /// host's registered ShiftMapper mapper (<see cref="ShiftMapper.IMapper"/>), which must declare
    /// <c>CreateMap&lt;Entity, CosmosDbItemReference&gt;()</c>.
    /// </param>
    public CosmosDbTriggerReferenceOperations<Entity> UpdatePropertyReference<CosmosDbItemReference, DestinationContainer>(
        string cosmosContainerId, Expression<Func<DestinationContainer, object>> destinationReferencePropertyExpression,
        Func<IQueryable<DestinationContainer>, EntityWrapper<Entity>, IQueryable<DestinationContainer>> finder,
        Func<EntityWrapper<Entity>, CosmosDbItemReference>? mapping = null)
    {
        if (this.treeAction is not null)
            throw NoReferencesWithTree();

        string propertyPath = Utility.GetPropertyFullPath(destinationReferencePropertyExpression);

        this.cosmosContainerIds.Add(cosmosContainerId);

        this.upsertReferenceActions.Add(async (entity, services, db) =>
        {
            //Resolved above the loop and before the lookup — see UpdateReference.
            var create = mapping is null
                ? ReplicationMapper.ResolveCreate<Entity, CosmosDbItemReference>(services,
                    $"UpdatePropertyReference<{typeof(CosmosDbItemReference).Name}, {typeof(DestinationContainer).Name}>(\"{cosmosContainerId}\")")
                : null;

            bool? isSucceeded = null;
            string? failure = null;
            var container = db.GetContainer(cosmosContainerId);

            var items = await GetItemsForReferenceUpdate(container, entity, services, finder);

            if (items is null || items?.Count() == 0)
                return (true, null);

            List<Task> cosmosTasks = new();

            var containerReposne = await container.ReadContainerAsync();

            foreach (var item in items)
            {
                CosmosDbItemReference propertyItem = mapping is not null
                    ? mapping(new EntityWrapper<Entity>(entity, services))
                    : create!(entity);

                var id = Convert.ToString(item.GetProperty("id"));
                PartitionKey partitionKey = Utility.GetPartitionKey(containerReposne, item!);

                cosmosTasks.Add(container.PatchItemAsync<DestinationContainer>(id, partitionKey,
                        new[] { PatchOperation.Replace($"/{propertyPath}", propertyItem) })
                    .ContinueWith(x =>
                    {
                        isSucceeded = isSucceeded.GetValueOrDefault(true) && x.IsCompletedSuccessfully;
                        if (!x.IsCompletedSuccessfully)
                            Interlocked.CompareExchange(ref failure, ReferenceFailed(cosmosContainerId, x.Exception), null);
                    }));
            }

            await Task.WhenAll(cosmosTasks);

            return (isSucceeded.GetValueOrDefault(false), failure);
        });

        return this;
    }

    private async Task<IEnumerable<CosmosDBItem>> GetItemsForReferenceUpdate<CosmosDBItem>(Container container,
        Entity entity, IServiceProvider serviceProvider,
        Func<IQueryable<CosmosDBItem>, EntityWrapper<Entity>, IQueryable<CosmosDBItem>> finder)
    {
        var query = container.GetItemLinqQueryable<CosmosDBItem>().AsQueryable();

        query = finder(query, new EntityWrapper<Entity>(entity, serviceProvider));

        var feedIterator = query.ToFeedIterator<CosmosDBItem>();

        List<CosmosDBItem> items = new();

        while (feedIterator.HasMoreResults)
        {
            items.AddRange(await feedIterator.ReadNextAsync());
        }

        return items;
    }

    internal async Task RunAsync(Entity entity, IServiceProvider serviceProvider, ReplicationChangeType changeType)
    {
        bool? isSucceeded = null;
        string? replicationStamp = null;
        string? failure = null;

        //Do the mapping if not null
        if (setupMapping is not null)
            entity = await setupMapping(new EntityWrapper<Entity>(entity, serviceProvider));

        //The row and the version this sync replicates, read before anything maps the entity. The watermark is this
        //version's save date and never a later one: a save that moves LastSaveDate while this sync runs keeps the row
        //due for the next sync.
        var id = entity.ID;
        var replicatedVersion = entity.LastSaveDate;

        var db = await this.DatabaseAsync();

        //Hard delete → remove the entity's own document from Cosmos and stop. No bookkeeping write afterwards: the SQL
        //row is already deleted.
        if (changeType == ReplicationChangeType.Deleted)
        {
            if (deleteAction is not null)
                await deleteAction(entity, serviceProvider, db);
            return;
        }

        //If the change type is added or modified, then do the upsert action
        //(it removes the stale document first when the last written stamp shows the id/partition key changed)
        if ((changeType == ReplicationChangeType.Added || changeType == ReplicationChangeType.Modified) && this.replicateAction is not null)
        {
            //Reset the isSucceeded flag
            isSucceeded = isSucceeded.GetValueOrDefault(true) ? null : false;

            var result = await this.replicateAction(entity, serviceProvider, db);

            isSucceeded = isSucceeded.GetValueOrDefault(true) && result.success;
            replicationStamp = result.stamp;
            failure ??= result.failure;
        }

        //The tree document: built from the root loaded by ID, never from the caller's instance. Its stamp is the row's,
        //and the version it loaded is the one this sync replicates. A root it cannot load records nothing.
        if ((changeType == ReplicationChangeType.Added || changeType == ReplicationChangeType.Modified) && this.treeAction is not null)
        {
            isSucceeded = isSucceeded.GetValueOrDefault(true) ? null : false;

            if (await this.treeAction(id, serviceProvider, db) is not { } tree)
                return;

            isSucceeded = isSucceeded.GetValueOrDefault(true) && tree.Success;
            replicationStamp = tree.Stamp;
            replicatedVersion = tree.Version;
            failure ??= tree.Failure;
        }

        if (changeType == ReplicationChangeType.Modified)
        {
            foreach (var action in this.upsertReferenceActions)
            {
                //Reset the isSucceeded flag
                isSucceeded = isSucceeded.GetValueOrDefault(true) ? null : false;

                var result = await action(entity, serviceProvider, db);

                isSucceeded = isSucceeded.GetValueOrDefault(true) && result.success;
                failure ??= result.failure;
            }
        }

        //A failed step records nothing: the row stays dirty and the catch-up sync retries it. The sync then fails with
        //the step's reason, so that it is logged as failed and not as succeeded.
        if (isSucceeded == false)
            throw SyncFailed(id, failure);

        //No step ran.
        if (isSucceeded is null)
            return;

        //Kept for this instance before the write below: the document is at these coordinates now, whether or not the
        //write succeeds.
        if (replicationStamp is not null)
            writtenStamps.AddOrUpdate(entity, replicationStamp);

        //Written by key, from the values read at the start, and never through the entity. The entity is the caller's
        //instance, still tracked by the caller's context and possibly linked to rows the caller has added since the
        //save that triggered this sync. Attaching it would insert those rows a second time. Setting the two columns on
        //it would make the caller's next save write the row again and replicate it once more.
        var dbContext = (ShiftDbContext)serviceProvider.GetRequiredService(this.dbContextType);
        await dbContext.SaveReplicationBookkeepingAsync<Entity>(id, replicatedVersion, replicationStamp);
    }

    /// <summary>
    /// Replicates the tree document of the root with key <paramref name="id"/>, which a save changed through another
    /// row of its tree. The root is loaded by ID with its tree. On success the watermark is the loaded version and the
    /// stamp the document's. On failure nothing is recorded, and the sync fails with the reason: the save marked the
    /// root as not replicated, so the catch-up retries it.
    /// </summary>
    internal async Task RunTreeAsync(long id, IServiceProvider serviceProvider)
    {
        var db = await this.DatabaseAsync();
        if (await this.treeAction!(id, serviceProvider, db) is not { } tree)
            return;

        if (!tree.Success)
            throw SyncFailed(id, tree.Failure);

        //LOUD: this watermark is the root's LastSaveDate as loaded, and a change of a part never moves LastSaveDate. So
        //it clears any mark of the root, also one that a later change of its tree made after this sync loaded it. If
        //the sync of that later change fails, or lands before this one, the document keeps this older tree while the
        //root looks replicated, until its next save or a forced catch-up. Scripts and imports that edit tree parts must
        //run a forced catch-up afterwards.
        var dbContext = (ShiftDbContext)serviceProvider.GetRequiredService(this.dbContextType);
        await dbContext.SaveReplicationBookkeepingAsync<Entity>(id, tree.Version, tree.Stamp);
    }

    private async Task<Database> DatabaseAsync()
    {
        //Prepare the lists of the container that used, to the connection
        if (this.client is null)
        {
            List<(string databaseId, string continerId)> containers = new();
            foreach (var containerId in this.cosmosContainerIds)
                containers.Add((this.cosmosDataBaseId, containerId));

            //Connect to the cosmos selected database and containers
            this.client = await CosmosClient.CreateAndInitializeAsync(this.cosmosDbConnectionString, containers);
        }

        //Set the client to allow bulk execution
        client.ClientOptions.AllowBulkExecution = true;

        return client.GetDatabase(this.cosmosDataBaseId);
    }
}

public class EntityWrapper<EntityType>
    where EntityType : ShiftEntity<EntityType>
{
    public EntityWrapper(EntityType entity, IServiceProvider serviceProvider)
    {
        Entity = entity;
        Services = serviceProvider;
    }

    public EntityType Entity { get; }
    public IServiceProvider Services { get; }
}
