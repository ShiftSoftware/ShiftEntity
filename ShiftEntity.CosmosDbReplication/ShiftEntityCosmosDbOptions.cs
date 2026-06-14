using AutoMapper;
using EntityFrameworkCore.Triggered;
using EntityFrameworkCore.Triggered.Extensions;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.CosmosDbReplication.Exceptions;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.EFCore.Entities;
using ShiftSoftware.ShiftEntity.Model.Enums;
using ShiftSoftware.ShiftEntity.Model.Replication;
using System.Linq.Expressions;
using System.Net;
using System.Reflection;

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
    /// <param name="mapping">If null, it use auto mapper to map it</param>
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
    /// <param name="mapping">If null, it use auto mapper to map it</param>
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
    /// <param name="mapping">If null, it use auto mapper to map it</param>
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

    private Func<Entity, IServiceProvider, Database, Task<(bool success, string? stamp)>> replicateAction;
    private List<Func<Entity, IServiceProvider, Database, Task<bool?>>> upsertReferenceActions = new();

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
            CosmosDbItem item;

            if (mapping is not null)
                item = mapping(new EntityWrapper<Entity>(entity, services));
            else
            {
                var autoMapper = services.GetRequiredService<IMapper>();
                item = autoMapper.Map<CosmosDbItem>(entity);
            }

            var container = db.GetContainer(cosmosContainerId);

            //Detect an id or partition-key change since the last successful sync and remove the stale Cosmos
            //document under the OLD id + key before upserting under the new one (Cosmos can't mutate a document's
            //id or PK, so a naive upsert would orphan the old doc). The OLD coordinates come from the entity's
            //persisted LastReplicationStamp — what was actually written to Cosmos last time — never from
            //re-mapping the pre-save entity snapshot: a mapping that leaves a partition-key component unset (or
            //derives values from navigations) reconstructs coordinates that don't match the stored document, so
            //such a delete silently misses (404) and the old document is orphaned.
            string? staleId = null;
            PartitionKey? stalePartitionKey = null;

            var containerResponse = await container.ReadContainerAsync();
            var newStamp = Utility.BuildStamp(containerResponse, item!);
            var oldStamp = LastReplicationStamp.Deserialize(entity.LastReplicationStamp);

            if (oldStamp is not null && newStamp.DiffersFrom(oldStamp))
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
                catch
                {
                    //Report failure WITHOUT upserting: the stamp/replication date stay untouched, the row stays
                    //dirty, and the catch-up sync retries the delete + upsert later.
                    return (false, null);
                }
            }

            var response = await container.UpsertItemAsync(item);

            bool success = response.StatusCode == System.Net.HttpStatusCode.OK ||
                    response.StatusCode == System.Net.HttpStatusCode.Created ||
                    response.StatusCode == System.Net.HttpStatusCode.NoContent;

            //On success, RunAsync persists the new stamp (the id + partition key this row now lives under in
            //Cosmos) on the entity, so the NEXT sync — trigger or catch-up — can detect the next change.
            return (success, success ? stamp : null);
        };

    }

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

    public CosmosDbTriggerReferenceOperations<Entity> UpdateReference<CosmosDbItem>(string cosmosContainerId,
                Func<IQueryable<CosmosDbItem>, EntityWrapper<Entity>, IQueryable<CosmosDbItem>> finder,
                Func<EntityWrapper<Entity>, CosmosDbItem, CosmosDbItem>? mapping = null)
    {
        this.cosmosContainerIds.Add(cosmosContainerId);

        this.upsertReferenceActions.Add(async (entity, services, db) =>
        {
            bool? isSucceeded = null;
            var container = db.GetContainer(cosmosContainerId);

            var items = await GetItemsForReferenceUpdate(container, entity, services, finder);

            if(items is null || items?.Count() == 0)
                return true;

            List<Task> cosmosTasks = new();

            foreach (var item in items)
            {
                CosmosDbItem tempItems = item;
                if (mapping is null)
                {
                    var mapper = services.GetRequiredService<IMapper>();
                    mapper.Map(entity, tempItems);
                }
                else
                    tempItems = mapping(new EntityWrapper<Entity>(entity, services), item);

                cosmosTasks.Add(container.UpsertItemAsync<CosmosDbItem>(tempItems)
                    .ContinueWith(x =>
                    {
                        isSucceeded = isSucceeded.GetValueOrDefault(true) && x.IsCompletedSuccessfully;
                    }));
            }

            await Task.WhenAll(cosmosTasks);

            return isSucceeded;
        });

        return this;
    }

    public CosmosDbTriggerReferenceOperations<Entity> UpdatePropertyReference<CosmosDbItemReference, DestinationContainer>(
        string cosmosContainerId, Expression<Func<DestinationContainer, object>> destinationReferencePropertyExpression,
        Func<IQueryable<DestinationContainer>, EntityWrapper<Entity>, IQueryable<DestinationContainer>> finder,
        Func<EntityWrapper<Entity>, CosmosDbItemReference>? mapping = null)
    {
        string propertyPath = Utility.GetPropertyFullPath(destinationReferencePropertyExpression);

        this.cosmosContainerIds.Add(cosmosContainerId);

        this.upsertReferenceActions.Add(async (entity, services, db) =>
        {
            bool? isSucceeded = null;
            var container = db.GetContainer(cosmosContainerId);

            var items = await GetItemsForReferenceUpdate(container, entity, services, finder);

            if (items is null || items?.Count() == 0)
                return true;

            List<Task> cosmosTasks = new();

            var containerReposne = await container.ReadContainerAsync();

            foreach (var item in items)
            {
                CosmosDbItemReference propertyItem;
                if (mapping is null)
                {
                    var mapper = services.GetRequiredService<IMapper>();
                    propertyItem = mapper.Map<CosmosDbItemReference>(entity);
                }
                else
                    propertyItem = mapping(new EntityWrapper<Entity>(entity, services));

                var id = Convert.ToString(item.GetProperty("id"));
                PartitionKey partitionKey = Utility.GetPartitionKey(containerReposne, item!);

                cosmosTasks.Add(container.PatchItemAsync<DestinationContainer>(id, partitionKey,
                        new[] { PatchOperation.Replace($"/{propertyPath}", propertyItem) })
                    .ContinueWith(x =>
                    {
                        isSucceeded = isSucceeded.GetValueOrDefault(true) && x.IsCompletedSuccessfully;
                    }));
            }

            await Task.WhenAll(cosmosTasks);

            return isSucceeded;
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

    internal async Task RunAsync(Entity entity, IServiceProvider serviceProvider, ChangeType changeType)
    {
        bool? isSucceeded = null;
        string? replicationStamp = null;

        //Do the mapping if not null
        if (setupMapping is not null)
            entity = await setupMapping(new EntityWrapper<Entity>(entity, serviceProvider));

        //Prepare the lists of the container that used, to the connection
        if(this.client is null)
        {
            List<(string databaseId, string continerId)> containers = new();
            foreach (var containerId in this.cosmosContainerIds)
                containers.Add((this.cosmosDataBaseId, containerId));

            //Connect to the cosmos selected database and containers
            this.client = await CosmosClient.CreateAndInitializeAsync(this.cosmosDbConnectionString, containers);
        }

        //Set the client to allow bulk execution
        client.ClientOptions.AllowBulkExecution = true;

        var db = client.GetDatabase(this.cosmosDataBaseId);

        //If the change type is added or modified, then do the upsert action
        //(it removes the stale document first when the persisted stamp shows the id/partition key changed)
        if (changeType == ChangeType.Added || changeType == ChangeType.Modified)
        {
            //Reset the isSucceeded flag
            isSucceeded = isSucceeded.GetValueOrDefault(true) ? null : false;

            var result = await this.replicateAction(entity, serviceProvider, db);

            isSucceeded = isSucceeded.GetValueOrDefault(true) && result.success;
            replicationStamp = result.stamp;
        }

        if (changeType == ChangeType.Modified)
        {
            foreach (var action in this.upsertReferenceActions)
            {
                //Reset the isSucceeded flag
                isSucceeded = isSucceeded.GetValueOrDefault(true) ? null : false;

                var result = await action(entity, serviceProvider, db);

                isSucceeded = isSucceeded.GetValueOrDefault(true) && result.GetValueOrDefault(false);
            }
        }

        var dbContext = (ShiftDbContext)serviceProvider.GetRequiredService(this.dbContextType);

        if (isSucceeded.GetValueOrDefault(false))
            ApplyReplicationBookkeeping(dbContext, entity, replicationStamp);

        //A replication-bookkeeping save: only the replication columns marked modified above may be written. No
        //triggers, and no audit backfill — explicit suppression rather than relying on the attached entity's
        //in-memory AuditFieldsAreSet flag happening to be set from the user's original save.
        using (dbContext.SuppressAuditStamping())
            await dbContext.SaveChangesWithoutTriggersAsync();
    }

    private void ApplyReplicationBookkeeping(ShiftDbContext dbContext, Entity entity, string? stamp)
    {
        entity.MarkReplicated(stamp);

        dbContext.Attach(entity);
        dbContext.Entry(entity).Property(nameof(IShiftEntityReplication.LastReplicationDate)).IsModified = true;
        dbContext.Entry(entity).Property(nameof(IShiftEntityReplication.LastReplicationStamp)).IsModified = true;
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
