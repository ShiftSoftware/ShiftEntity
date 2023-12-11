using AutoMapper;
using EntityFrameworkCore.Triggered;
using EntityFrameworkCore.Triggered.Extensions;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.CosmosDbReplication.Exceptions;
using ShiftSoftware.ShiftEntity.CosmosDbReplication.Services;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.EFCore.Entities;
using System.Linq.Expressions;
using System.Net;
using System.Reflection;

namespace ShiftSoftware.ShiftEntity.CosmosDbReplication;

public class ShiftEntityCosmosDbOptions
{
    internal IServiceCollection Services { get; set; }

    public CosmosDbTriggerReplicateOperation<Entity> SetUpReplication<DB, Entity>(string cosmosDbConnectionString, string cosmosDataBaseId,
        Func<EntityWrapper<Entity>, ValueTask<Entity>>? mapper = null, bool removeDeleteRowLog = true)
        where Entity : ShiftEntity<Entity>
        where DB : ShiftDbContext
    {
        return new(cosmosDbConnectionString, cosmosDataBaseId, mapper, this.Services, typeof(DB), removeDeleteRowLog);
    }
}

public class CosmosDbTriggerReplicateOperation<Entity>
    where Entity : ShiftEntity<Entity>
{
    private readonly CosmosDbTriggerReferenceOperations<Entity> cosmosDbTriggerReferenceOperations;

    public CosmosDbTriggerReplicateOperation(string cosmosDbConnectionString, string cosmosDataBaseId,
        Func<EntityWrapper<Entity>, ValueTask<Entity>>? setupMapping, IServiceCollection services, Type dbContextType, bool removeDeleteRowLog)
    {
        this.cosmosDbTriggerReferenceOperations =
            new CosmosDbTriggerReferenceOperations<Entity>(cosmosDbConnectionString, cosmosDataBaseId, setupMapping,
            dbContextType, removeDeleteRowLog);
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
    where Entity : ShiftEntity<Entity>
{
    private readonly string cosmosDbConnectionString;
    private readonly string cosmosDataBaseId;
    private readonly Func<EntityWrapper<Entity>, ValueTask<Entity>>? setupMapping;
    internal readonly Type dbContextType;
    private readonly bool removeDeleteRowLog;
    private List<string> cosmosContainerIds = new();

    internal string ReplicateContainerId { get; private set; }
    internal Func<object,(object? value, Type type, string? propertyName)?>? PartitionKeyLevel1Action { get; private set; }
    internal Func<object,(object? value, Type type, string? propertyName)?>? PartitionKeyLevel2Action { get; private set; }
    internal Func<object,(object? value, Type type, string? propertyName)?>? PartitionKeyLevel3Action { get; private set; }
    internal Func<EntityWrapper<Entity>, object>? ReplicateMipping { get; private set; }
    internal Type ReplicateComsomsDbItemType { get; private set; }

    private Func<Entity, IServiceProvider, Database, Task<bool>> replicateAction;
    private Func<Entity, IServiceProvider, Database, Task<(long id, (PartitionKey? partitionKey,
        (string? value, PartitionKeyTypes type)? level1,
        (string? value, PartitionKeyTypes type)? level2,
        (string? value, PartitionKeyTypes type)? level3)? partitionKeyDetails, bool isSucceeded)>> replicateDeleteAction;
    private List<Func<Entity, IServiceProvider, Database, Task<bool?>>> deleteReferenceActions = new();
    private List<Func<Entity, IServiceProvider, Database, Task<bool?>>> upsertReferenceActions = new();

    internal CosmosDbTriggerReferenceOperations(string cosmosDbConnectionString, string cosmosDataBaseId,
        Func<EntityWrapper<Entity>, ValueTask<Entity>>? setupMapping, Type dbContextType, bool removeDeleteRowLog)
    {
        this.cosmosDbConnectionString = cosmosDbConnectionString;
        this.cosmosDataBaseId = cosmosDataBaseId;
        this.setupMapping = setupMapping;
        this.dbContextType = dbContextType;
        this.removeDeleteRowLog = removeDeleteRowLog;
    }

    internal void Replicate<CosmosDbItem>(
        string cosmosContainerId,
        Expression<Func<CosmosDbItem, object>> partitionKeyLevel1Expression,
        Expression<Func<CosmosDbItem, object>>? partitionKeyLevel2Expression,
        Expression<Func<CosmosDbItem, object>>? partitionKeyLevel3Expression,
        Func<EntityWrapper<Entity>, CosmosDbItem>? mapping)
    {
        this.cosmosContainerIds.Add(cosmosContainerId);
        this.ReplicateContainerId = cosmosContainerId;

        SetPartitonKeyActions(partitionKeyLevel1Expression, partitionKeyLevel2Expression, partitionKeyLevel3Expression);
        this.ReplicateMipping = mapping is not null ? new Func<EntityWrapper<Entity>, object>(x => mapping(x)!) : null;
        this.ReplicateComsomsDbItemType = typeof(CosmosDbItem);

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

            var response = await container.UpsertItemAsync(item);

            if (response.StatusCode == System.Net.HttpStatusCode.OK ||
                    response.StatusCode == System.Net.HttpStatusCode.Created ||
                    response.StatusCode == System.Net.HttpStatusCode.NoContent)
                return true;

            return false;
        };

        this.replicateDeleteAction = async (entity, services, db) =>
            {
                bool isSucceeded = false;
                CosmosDbItem item;

                if (mapping is not null)
                    item = mapping(new EntityWrapper<Entity>(entity, services));
                else
                {
                    var autoMapper = services.GetRequiredService<IMapper>();
                    item = autoMapper.Map<CosmosDbItem>(entity);
                }

                var container = db.GetContainer(cosmosContainerId);

                var containerResponse = await container.ReadContainerAsync();
                var partitionKeyDetails = PartitionKeyHelper.GetPartitionKeyDetails(containerResponse, item!);

                //Get item id
                var idString = Convert.ToString(item.GetProperty("id"));
                long id = 0;
                long.TryParse(idString, out id);

                await container.DeleteItemAsync<CosmosDbItem>(idString,
                    partitionKeyDetails.partitionKey ?? PartitionKey.None)
                    .ContinueWith(x =>
                    {
                        CosmosException ex = null;

                        if (x.Exception != null)
                            foreach (var innerException in x.Exception.InnerExceptions)
                                if (innerException is CosmosException customException)
                                    ex = customException;

                        isSucceeded = x.IsCompletedSuccessfully || ex?.StatusCode == HttpStatusCode.NotFound;
                    }).WaitAsync(new CancellationToken());

                return (id, partitionKeyDetails, isSucceeded);
            };
    }

    private void SetPartitonKeyActions<CosmosDbItem>(
        Expression<Func<CosmosDbItem, object>>? partitionKeyLevel1Expression,
        Expression<Func<CosmosDbItem, object>>? partitionKeyLevel2Expression,
        Expression<Func<CosmosDbItem, object>>? partitionKeyLevel3Expression)
    {
        if (partitionKeyLevel1Expression is not null)
        {
            this.PartitionKeyLevel1Action = o => GetPartitionKey(o, partitionKeyLevel1Expression);
            CheckPartitionKeyType(partitionKeyLevel1Expression);
        }

        if (partitionKeyLevel2Expression is not null)
        {
            this.PartitionKeyLevel2Action = o => GetPartitionKey(o, partitionKeyLevel2Expression);
            CheckPartitionKeyType(partitionKeyLevel2Expression);
        }

        if (partitionKeyLevel3Expression is not null)
        {
            this.PartitionKeyLevel3Action = o => GetPartitionKey(o, partitionKeyLevel3Expression);
            CheckPartitionKeyType(partitionKeyLevel3Expression);
        }
    }

    private (object? value, Type type, string? propertyName)? GetPartitionKey<CosmosDbItem>(object data, 
        Expression<Func<CosmosDbItem, object>> partitionKeyExpression)
    {
        var value = partitionKeyExpression.Compile().Invoke((CosmosDbItem)data);
        var type = partitionKeyExpression.Body.Type;
        var propertyName = null as string;

        // Check if the body of the expression is a UnaryExpression
        if (partitionKeyExpression.Body is UnaryExpression unaryExpression)
        {
            // If it is, use the Operand property to get the underlying expression
            type = unaryExpression.Operand.Type;
        }

        // Check if the body of the expression is a MemberExpression
        if (partitionKeyExpression.Body is MemberExpression memberExpression)
            propertyName = memberExpression.Member.Name;

        return (value, type, propertyName);
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

        this.deleteReferenceActions.Add(async (entity, services, db) =>
        {
            bool? isSucceeded = null;
            var container = db.GetContainer(cosmosContainerId);

            var items = await GetItemsForReferenceUpdate(container, entity, services, finder);

            if (items is null || items?.Count() == 0)
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

                var containerResponse = await container.ReadContainerAsync();
                var partitionKeyDetails = PartitionKeyHelper.GetPartitionKeyDetails(containerResponse, item!);

                //Get item id
                var idString = Convert.ToString(item.GetProperty("id"));

                cosmosTasks.Add(container.DeleteItemAsync<CosmosDbItem>(idString,
                    partitionKeyDetails.partitionKey ?? PartitionKey.None)
                    .ContinueWith(x =>
                    {
                        CosmosException ex = null;

                        if (x.Exception != null)
                            foreach (var innerException in x.Exception.InnerExceptions)
                                if (innerException is CosmosException customException)
                                    ex = customException;

                        isSucceeded = isSucceeded.GetValueOrDefault(true) &&
                            (x.IsCompletedSuccessfully || ex?.StatusCode == HttpStatusCode.NotFound);
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
        string propertyName = "";
        
        if (destinationReferencePropertyExpression.Body is MemberExpression memberExpression)
            propertyName = memberExpression.Member.Name;
        else
            throw new ArgumentException("Expression must be a member access expression");

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
                PartitionKey partitionKey = PartitionKeyHelper.GetPartitionKey(containerReposne, item!);

                cosmosTasks.Add(container.PatchItemAsync<DestinationContainer>(id, partitionKey,
                        new[] { PatchOperation.Replace($"/{propertyName}", propertyItem) })
                    .ContinueWith(x =>
                    {
                        isSucceeded = isSucceeded.GetValueOrDefault(true) && x.IsCompletedSuccessfully;
                    }));
            }

            await Task.WhenAll(cosmosTasks);

            return isSucceeded;
        });

        this.deleteReferenceActions.Add(async (entity, services, db) =>
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
                PartitionKey partitionKey = PartitionKeyHelper.GetPartitionKey(containerReposne, item!);

                cosmosTasks.Add(container.PatchItemAsync<DestinationContainer>(id, partitionKey,
                    new[] { PatchOperation.Remove($"/{propertyName}") })
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
        (long id, (PartitionKey? partitionKey,
            (string? value, PartitionKeyTypes type)? level1,
            (string? value, PartitionKeyTypes type)? level2,
            (string? value, PartitionKeyTypes type)? level3)? partitionKeyDetails,
            bool isSucceeded) replicateDeleteItemInfo = new();
        bool? isSucceeded = null;

        //Do the mapping if not null
        if (setupMapping is not null)
            entity = await setupMapping(new EntityWrapper<Entity>(entity, serviceProvider));

        //Prepare the lists of the container that used, to the connection
        List<(string databaseId, string continerId)> containers = new();
        foreach (var containerId in this.cosmosContainerIds)
            containers.Add((this.cosmosDataBaseId, containerId));

        //Connect to the cosmos selected database and containers
        using var client = await CosmosClient.CreateAndInitializeAsync(this.cosmosDbConnectionString, containers, new CosmosClientOptions()
        {
            AllowBulkExecution = true
        });
        var db = client.GetDatabase(this.cosmosDataBaseId);

        //If the change type is added or modified, then do the upsert action
        if (changeType == ChangeType.Added || changeType == ChangeType.Modified)
        {
            //Reset the isSucceeded flag
            isSucceeded = isSucceeded.GetValueOrDefault(true) ? null : false;

            var result = await this.replicateAction(entity, serviceProvider, db);

            isSucceeded = isSucceeded.GetValueOrDefault(true) && result;
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
        else if (changeType == ChangeType.Deleted)
        {
            //Reset the isSucceeded flag
            isSucceeded = isSucceeded.GetValueOrDefault(true) ? null : false;

            replicateDeleteItemInfo = await this.replicateDeleteAction(entity, serviceProvider, db);

            isSucceeded = isSucceeded.GetValueOrDefault(true) && replicateDeleteItemInfo.isSucceeded;

            foreach (var action in this.deleteReferenceActions)
            {
                //Reset the isSucceeded flag
                isSucceeded = isSucceeded.GetValueOrDefault(true) ? null : false;

                var result = await action(entity, serviceProvider, db);

                isSucceeded = isSucceeded.GetValueOrDefault(true) && result.GetValueOrDefault(false);
            }
        }

        var dbContext = (ShiftDbContext)serviceProvider.GetRequiredService(this.dbContextType);

        if (isSucceeded.GetValueOrDefault(false) && (changeType == ChangeType.Added || changeType == ChangeType.Modified))
            UpdateLastReplicationDateAsync(dbContext, entity);
        else if (isSucceeded.GetValueOrDefault(false) && changeType == ChangeType.Deleted)
            await UpdateDeleteRowAsync(dbContext, entity, replicateDeleteItemInfo.id, replicateDeleteItemInfo.partitionKeyDetails);

        await dbContext.SaveChangesWithoutTriggersAsync();
    }

    private void UpdateLastReplicationDateAsync(ShiftDbContext dbContext, Entity entity)
    {
        entity.UpdateReplicationDate();
        dbContext.Attach(entity);
        dbContext.Entry(entity).Property(nameof(ShiftEntity<object>.LastReplicationDate)).IsModified = true;
    }

    private async Task UpdateDeleteRowAsync(ShiftDbContext dbContext, Entity entity, long rowId, (PartitionKey? partitionKey,
        (string? value, PartitionKeyTypes type)? level1,
        (string? value, PartitionKeyTypes type)? level2,
        (string? value, PartitionKeyTypes type)? level3)? partitionKeyDetails)
    {
        var query = dbContext.DeletedRowLogs.Where(x =>
            x.RowID == rowId &&
            x.ContainerName == this.ReplicateContainerId
        );

        if (partitionKeyDetails?.level1 is not null)
            query = query.Where(x => x.PartitionKeyLevelOneValue == partitionKeyDetails.Value.level1.Value.value &&
                x.PartitionKeyLevelOneType == partitionKeyDetails.Value.level1.Value.type);

        if (partitionKeyDetails?.level2 is not null)
            query = query.Where(x => x.PartitionKeyLevelTwoValue == partitionKeyDetails.Value.level2.Value.value &&
                                    x.PartitionKeyLevelTwoType == partitionKeyDetails.Value.level2!.Value.type);

        if (partitionKeyDetails?.level3 is not null)
            query = query.Where(x => x.PartitionKeyLevelThreeValue == partitionKeyDetails.Value.level3!.Value.value &&
                x.PartitionKeyLevelThreeType == partitionKeyDetails.Value.level3!.Value.type);

        var log = await query.SingleOrDefaultAsync();
        
        if (log is not null)
            if(this.removeDeleteRowLog)
                dbContext.DeletedRowLogs.Remove(log);
            else
            {
                log.LastReplicationDate = DateTime.UtcNow;
                dbContext.Attach(log);
                dbContext.Entry(log).Property(nameof(DeletedRowLog.LastReplicationDate)).IsModified = true;
            }
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
