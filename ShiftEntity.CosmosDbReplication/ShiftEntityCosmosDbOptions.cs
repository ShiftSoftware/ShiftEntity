using AutoMapper;
using EntityFrameworkCore.Triggered;
using EntityFrameworkCore.Triggered.Extensions;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;
using Microsoft.Azure.Cosmos.Serialization.HybridRow;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Internal;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.CosmosDbReplication.Exceptions;
using ShiftSoftware.ShiftEntity.CosmosDbReplication.Services;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.EFCore.Entities;
using ShiftSoftware.ShiftEntity.Model;
using System.Linq.Expressions;
using System.Net;
using System.Reflection;

namespace ShiftSoftware.ShiftEntity.CosmosDbReplication;

public class ShiftEntityCosmosDbOptions
{
    internal IServiceCollection Services { get; set; }

    /// <summary>
    /// This is become the default connection
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// This is become the default database for the specified connection string
    /// </summary>
    public string? DefaultDatabaseName { get; set; }

    public List<CosmosDBAccount> Accounts { get; set; }

    internal List<ShiftDbContextStore> ShiftDbContextStorage { get; private set; }

    public ShiftEntityCosmosDbOptions()
    {
        Accounts = new();
        ShiftDbContextStorage = new();
    }

    public ShiftEntityCosmosDbOptions AddShiftDbContext<T>(Action<DbContextOptionsBuilder<T>> optionBuilder)
        where T : ShiftDbContext
    {
        DbContextOptionsBuilder<T> builder = new();
        optionBuilder.Invoke(builder);

        ShiftDbContextStorage.Add(new ShiftDbContextStore(typeof(T), builder.Options));
        return this;
    }

    public CosmosDbTriggerReplicateOperation<Entity> SetUpReplication<DB, Entity>(string cosmosDbConnectionString, string cosmosDataBaseId,
        Func<EntityWrapper<Entity>, ValueTask<Entity>>? mapper = null)
        where Entity : ShiftEntity<Entity>
        where DB : ShiftDbContext
    {
        return new(cosmosDbConnectionString, cosmosDataBaseId, mapper, this.Services, typeof(DB));
    }
}

public class CosmosDbTriggerReplicateOperation<Entity>
    where Entity : ShiftEntity<Entity>
{
    private readonly CosmosDbTriggerReferenceOperations<Entity> cosmosDbTriggerReferenceOperations;

    public CosmosDbTriggerReplicateOperation(string cosmosDbConnectionString, string cosmosDataBaseId,
        Func<EntityWrapper<Entity>, ValueTask<Entity>>? setupMapping, IServiceCollection services, Type dbContextType)
    {
        this.cosmosDbTriggerReferenceOperations =
            new CosmosDbTriggerReferenceOperations<Entity>(cosmosDbConnectionString, cosmosDataBaseId, setupMapping, dbContextType);
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
               Func<EntityWrapper<Entity>, CosmosDbItem>? mapping = null)
    {
        this.cosmosDbTriggerReferenceOperations.Replicate(cosmosContainerId, mapping);
        return this.cosmosDbTriggerReferenceOperations;
    }
}

public class CosmosDbTriggerReferenceOperations<Entity>
    where Entity : ShiftEntity<Entity>
{
    private readonly string cosmosDbConnectionString;
    private readonly string cosmosDataBaseId;
    private readonly Func<EntityWrapper<Entity>, ValueTask<Entity>>? setupMapping;
    private readonly Type dbContextType;
    private List<string> cosmosContainerIds = new();
    private string replicateContainerId;

    private Func<Entity, IServiceProvider, Database, Task<bool>> replicateAction;
    private Func<Entity, IServiceProvider, Database, Task<(long id, (PartitionKey? partitionKey,
        (string? value, PartitionKeyTypes type)? level1,
        (string? value, PartitionKeyTypes type)? level2,
        (string? value, PartitionKeyTypes type)? level3)? partitionKeyDetails, bool isSucceeded)>> replicateDeleteAction;
    private List<Func<Entity, IServiceProvider, Database, Task<bool?>>> deleteReferenceActions = new();
    private List<Func<Entity, IServiceProvider, Database, Task<bool?>>> upsertReferenceActions = new();

    internal CosmosDbTriggerReferenceOperations(string cosmosDbConnectionString, string cosmosDataBaseId,
        Func<EntityWrapper<Entity>, ValueTask<Entity>>? setupMapping, Type dbContextType)
    {
        this.cosmosDbConnectionString = cosmosDbConnectionString;
        this.cosmosDataBaseId = cosmosDataBaseId;
        this.setupMapping = setupMapping;
        this.dbContextType = dbContextType;
    }

    internal void Replicate<CosmosDbItem>(string cosmosContainerId,
               Func<EntityWrapper<Entity>, CosmosDbItem>? mapping)
    {
        this.cosmosContainerIds.Add(cosmosContainerId);
        this.replicateContainerId = cosmosContainerId;

        this.replicateAction = async (entity, services, db) =>
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

            var response = await container.UpsertItemAsync(item);

            if (response.StatusCode == System.Net.HttpStatusCode.OK ||
                response.StatusCode == System.Net.HttpStatusCode.Created ||
                response.StatusCode == System.Net.HttpStatusCode.NoContent)
                isSucceeded = true;
            else
                isSucceeded = false;

            return isSucceeded;
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

            if (items is not null && items?.Count() > 0)
            {
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
            }

            return isSucceeded;
        });

        this.deleteReferenceActions.Add(async (entity, services, db) =>
        {
            bool? isSucceeded = null;
            var container = db.GetContainer(cosmosContainerId);

            var items = await GetItemsForReferenceUpdate(container, entity, services, finder);

            if (items is not null && items?.Count() > 0)
            {
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
            }

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

            if (items is not null && items?.Count() > 0)
            {
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
            }

            return isSucceeded;
        });

        this.deleteReferenceActions.Add(async (entity, services, db) =>
        {
            bool? isSucceeded = null;
            var container = db.GetContainer(cosmosContainerId);

            var items = await GetItemsForReferenceUpdate(container, entity, services, finder);

            if (items is not null && items?.Count() > 0)
            {
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
            }

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

        //Connect to the cosmos selected database
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
            await RemoveDeleteRowAsync(dbContext, entity, replicateDeleteItemInfo.id, replicateDeleteItemInfo.partitionKeyDetails);

        await dbContext.SaveChangesWithoutTriggersAsync();
    }

    private void UpdateLastReplicationDateAsync(ShiftDbContext dbContext, Entity entity)
    {
        entity.UpdateReplicationDate();
        dbContext.Attach(entity);
        dbContext.Entry(entity).Property(nameof(ShiftEntity<object>.LastReplicationDate)).IsModified = true;
    }

    private async Task RemoveDeleteRowAsync(ShiftDbContext dbContext, Entity entity, long rowId, (PartitionKey? partitionKey,
        (string? value, PartitionKeyTypes type)? level1,
        (string? value, PartitionKeyTypes type)? level2,
        (string? value, PartitionKeyTypes type)? level3)? partitionKeyDetails)
    {
        var query = dbContext.DeletedRowLogs.Where(x =>
            x.RowID == rowId &&
            x.ContainerName == this.replicateContainerId
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
            dbContext.DeletedRowLogs.Remove(log);
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

internal class ShiftDbContextStore
{
    public Type ShiftDbContextType { get; private set; }
    public object DbContextOptions { get; private set; }

    public ShiftDbContextStore(Type shiftDbContextType, object dbContextOptions)
    {
        ShiftDbContextType = shiftDbContextType;
        DbContextOptions = dbContextOptions;
    }
}
