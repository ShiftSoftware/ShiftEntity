using AutoMapper;
using EntityFrameworkCore.Triggered;
using EntityFrameworkCore.Triggered.Extensions;
using Microsoft.Azure.Cosmos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Internal;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.CosmosDbReplication.Services;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.Model;
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
        Func<EntityWrapper<Entity>, ValueTask<Entity>>? setupMapper, IServiceCollection services, Type dbContextType)
    {
        this.cosmosDbTriggerReferenceOperations =
            new CosmosDbTriggerReferenceOperations<Entity>(cosmosDbConnectionString, cosmosDataBaseId, setupMapper, dbContextType);
        services.AddScoped(x => this.cosmosDbTriggerReferenceOperations);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="CosmosDbItem"></typeparam>
    /// <param name="cosmosContainerId"></param>
    /// <param name="mapper">If null, it use auto mapper to map it</param>
    /// <returns></returns>
    public CosmosDbTriggerReferenceOperations<Entity> Replicate<CosmosDbItem>(string cosmosContainerId,
               Func<EntityWrapper<Entity>, CosmosDbItem>? mapper = null)
    {
        this.cosmosDbTriggerReferenceOperations.Replicate(cosmosContainerId, mapper);
        return this.cosmosDbTriggerReferenceOperations;
    }
}

public class CosmosDbTriggerReferenceOperations<Entity>
    where Entity : ShiftEntity<Entity>
{
    private readonly string cosmosDbConnectionString;
    private readonly string cosmosDataBaseId;
    private readonly Func<EntityWrapper<Entity>, ValueTask<Entity>>? setupMapper;
    private readonly Type dbContextType;
    private Database db;
    private List<string> cosmosContainerIds = new();
    private string replicateContainerId;
    private IServiceProvider serviceProvider;

    private Func<Entity, IServiceProvider, Database, Task> replicateAction;
    private Func<Entity, IServiceProvider, Database, Task<(long id, (PartitionKey? partitionKey,
        (string? value, PartitionKeyTypes type)? level1,
        (string? value, PartitionKeyTypes type)? level2,
        (string? value, PartitionKeyTypes type)? level3)? partitionKeyDetails)>> replicateDeleteAction;
    private List<Func<Entity, IServiceProvider, Database, Task>> deleteReferenceActions = new();
    private List<Func<Task>> upsertReferenceActions = new();

    private bool? isSucceeded = null;

    private Entity entity;

    (PartitionKey? partitionKey,
        (string? value, PartitionKeyTypes type)? level1,
        (string? value, PartitionKeyTypes type)? level2,
        (string? value, PartitionKeyTypes type)? level3)? partitionKeyDetails = null;

    internal CosmosDbTriggerReferenceOperations(string cosmosDbConnectionString, string cosmosDataBaseId,
        Func<EntityWrapper<Entity>, ValueTask<Entity>>? setupMapper, Type dbContextType)
    {
        this.cosmosDbConnectionString = cosmosDbConnectionString;
        this.cosmosDataBaseId = cosmosDataBaseId;
        this.setupMapper = setupMapper;
        this.dbContextType = dbContextType;
    }

    internal void Replicate<CosmosDbItem>(string cosmosContainerId,
               Func<EntityWrapper<Entity>, CosmosDbItem>? mapper)
    {
        this.cosmosContainerIds.Add(cosmosContainerId);
        this.replicateContainerId = cosmosContainerId;

        this.replicateAction = async (entity, services, db) =>
        {
            CosmosDbItem item;

            if (mapper is not null)
                item = mapper(new EntityWrapper<Entity>(entity, this.serviceProvider));
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
                this.isSucceeded = this.isSucceeded.GetValueOrDefault(true) && true;
            else
                this.isSucceeded = this.isSucceeded.GetValueOrDefault(true) && false;
        };

        this.replicateDeleteAction = async (entity, services, db) =>
            {
                CosmosDbItem item;

                if (mapper is not null)
                    item = mapper(new EntityWrapper<Entity>(entity, this.serviceProvider));
                else
                {
                    var autoMapper = services.GetRequiredService<IMapper>();
                    item = autoMapper.Map<CosmosDbItem>(entity);
                }

                var container = db.GetContainer(cosmosContainerId);

                //Store the partition key details
                var containerResponse = await container.ReadContainerAsync();
                this.partitionKeyDetails = PartitionKeyHelper.GetPartitionKeyDetails(containerResponse, item!);

                //Get item id
                var idString = GetId(item);
                long id = 0;
                long.TryParse(idString, out id);

                await container.DeleteItemAsync<CosmosDbItem>(idString,
                    this.partitionKeyDetails?.partitionKey ?? PartitionKey.None)
                    .ContinueWith(x =>
                    {
                        CosmosException ex = null;

                        if (x.Exception != null)
                            foreach (var innerException in x.Exception.InnerExceptions)
                                if (innerException is CosmosException customException)
                                    ex = customException;

                        bool success = x.IsCompletedSuccessfully || ex?.StatusCode == HttpStatusCode.NotFound;
                        this.isSucceeded = this.isSucceeded.GetValueOrDefault(true) && success;
                    }).WaitAsync(new CancellationToken());

                return (id, partitionKeyDetails);
            };
    }

    internal async Task RunAsync(Entity entity, IServiceProvider serviceProvider, ChangeType changeType)
    {
        (long id, (PartitionKey? partitionKey,
        (string? value, PartitionKeyTypes type)? level1,
        (string? value, PartitionKeyTypes type)? level2,
        (string? value, PartitionKeyTypes type)? level3)? partitionKeyDetails) replicateDeleteItemInfo = new();

        this.serviceProvider = serviceProvider;

        //Do the mapping if not null
        if (setupMapper is not null)
            entity = await setupMapper(new EntityWrapper<Entity>(entity, serviceProvider));

        this.entity = entity;

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
        this.db = db;

        //If the change type is added or modified, then do the upsert action
        if (changeType == ChangeType.Added || changeType == ChangeType.Modified)
        {
            //Reset the isSucceeded flag
            this.isSucceeded = this.isSucceeded.GetValueOrDefault(true) ? null : false;

            await this.replicateAction(entity, serviceProvider, db);
        }

        if (changeType == ChangeType.Modified)
        {

        }
        else if (changeType == ChangeType.Deleted)
        {
            //Reset the isSucceeded flag
            this.isSucceeded = this.isSucceeded.GetValueOrDefault(true) ? null : false;

            replicateDeleteItemInfo = await this.replicateDeleteAction(entity, serviceProvider, db);

            foreach (var action in this.deleteReferenceActions)
            {
                //Reset the isSucceeded flag
                this.isSucceeded = this.isSucceeded.GetValueOrDefault(true) ? null : false;

                await action(entity, serviceProvider, db);
            }
        }

        var dbContext = (ShiftDbContext)serviceProvider.GetRequiredService(this.dbContextType);

        if (this.isSucceeded.GetValueOrDefault(false) && (changeType == ChangeType.Added || changeType == ChangeType.Modified))
            UpdateLastReplicationDateAsync(dbContext, entity);
        else if (this.isSucceeded.GetValueOrDefault(false) && changeType == ChangeType.Deleted)
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

    //public void Dispose()
    //{
    //    this.serviceProvider = null;
    //    this.db = null;
    //    this.cosmosContainerIds = null;
    //    this.replicateContainerId = null;
    //    this.replicateAction = null;
    //    this.deleteActions = null;
    //    this.referenceActions = null;
    //    this.isSucceeded = null;
    //    this.entity = null;
    //    this.partitionKeyDetails = null;
    //}

    private string? GetId(object obj)
    {
        // Get the type of the object
        Type objectType = obj.GetType();

        // Find the property by name (replace "PropertyName" with your property name)
        PropertyInfo? propertyInfo = objectType.GetProperty("id");

        if (propertyInfo is not null)
            return Convert.ToString(propertyInfo.GetValue(obj));
        else
            throw new MemberAccessException($"Can not find id property in the {objectType.Name}");
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
