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
        Func<Entity, IServiceProvider, ValueTask<Entity>>? mapper = null)
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
        Func<Entity, IServiceProvider, ValueTask<Entity>>? mapper, IServiceCollection services, Type dbContextType)
    {
        this.cosmosDbTriggerReferenceOperations =
            new CosmosDbTriggerReferenceOperations<Entity>(cosmosDbConnectionString, cosmosDataBaseId, mapper, dbContextType);
        services.AddSingleton(this.cosmosDbTriggerReferenceOperations);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="CosmosDbItem"></typeparam>
    /// <param name="cosmosContainerId"></param>
    /// <param name="mapper">If null, it use auto mapper to map it</param>
    /// <returns></returns>
    public CosmosDbTriggerReferenceOperations<Entity> Replicate<CosmosDbItem>(string cosmosContainerId,
               Func<Entity, IServiceProvider, CosmosDbItem>? mapper = null)
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
    private readonly Func<Entity, IServiceProvider, ValueTask<Entity>>? setupMapper;
    private readonly Type dbContextType;
    private Database db;
    private List<string> cosmosContainerIds = new();
    private string replicateContainerId;
    private IServiceProvider serviceProvider;

    private Func<Task> replicateAction;
    private List<Func<Task>> deleteActions = new();
    private List<Func<Task>> referenceActions = new();

    private bool? isSucceeded = null;

    private Entity entity;

    (PartitionKey? partitionKey,
        (string? value, PartitionKeyTypes type)? level1,
        (string? value, PartitionKeyTypes type)? level2,
        (string? value, PartitionKeyTypes type)? level3)? partitionKeyDetails = null;

    internal CosmosDbTriggerReferenceOperations(string cosmosDbConnectionString, string cosmosDataBaseId,
        Func<Entity, IServiceProvider, ValueTask<Entity>>? mapper, Type dbContextType)
    {
        this.cosmosDbConnectionString = cosmosDbConnectionString;
        this.cosmosDataBaseId = cosmosDataBaseId;
        this.setupMapper = mapper;
        this.dbContextType = dbContextType;
    }

    internal void Replicate<CosmosDbItem>(string cosmosContainerId,
               Func<Entity, IServiceProvider, CosmosDbItem>? mapper)
    {
        this.cosmosContainerIds.Add(cosmosContainerId);
        this.replicateContainerId = cosmosContainerId;

        this.replicateAction = async () =>
        {
            CosmosDbItem item;

            if (mapper is not null)
                item = mapper(this.entity, this.serviceProvider);
            else
            {
                var autoMapper = this.serviceProvider.GetRequiredService<IMapper>();
                item = autoMapper.Map<CosmosDbItem>(this.entity);
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

        this.deleteActions.Add(async () =>
            {
                CosmosDbItem item;

                if (mapper is not null)
                    item = mapper(this.entity, this.serviceProvider);
                else
                {
                    var autoMapper = this.serviceProvider.GetRequiredService<IMapper>();
                    item = autoMapper.Map<CosmosDbItem>(this.entity);
                }

                var container = this.db.GetContainer(cosmosContainerId);

                //Store the partition key details
                var containerResponse = await container.ReadContainerAsync();
                this.partitionKeyDetails = PartitionKeyHelper.GetPartitionKeyDetails(containerResponse, item!);

                //var response = await container.DeleteItemAsync<CosmosDbItem>(this.entity.ID.ToString(),
                //    this.partitionKeyDetails?.partitionKey ?? PartitionKey.None);

                //if (response.StatusCode == System.Net.HttpStatusCode.OK ||
                //        response.StatusCode == System.Net.HttpStatusCode.NotFound ||
                //        response.StatusCode == System.Net.HttpStatusCode.NoContent)
                //    this.isSucceeded = this.isSucceeded.GetValueOrDefault(true) && true;
                //else
                //    this.isSucceeded = this.isSucceeded.GetValueOrDefault(true) && false;

                await container.DeleteItemAsync<CosmosDbItem>(this.entity.ID.ToString(),
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
            });
    }

    internal async Task RunAsync(Entity entity, IServiceProvider serviceProvider, ChangeType changeType)
    {
        this.serviceProvider = serviceProvider;

        //Do the mapping if not null
        if (setupMapper is not null)
            entity = await setupMapper.Invoke(entity, serviceProvider);

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
        this.db = client.GetDatabase(this.cosmosDataBaseId);

        //If the change type is added or modified, then do the upsert action
        if (changeType == ChangeType.Added || changeType == ChangeType.Modified)
        {
            //Reset the isSucceeded flag
            this.isSucceeded = this.isSucceeded.GetValueOrDefault(true) ? null : false;

            await this.replicateAction.Invoke();
        }

        if (changeType == ChangeType.Modified)
        {

        }
        else if (changeType == ChangeType.Deleted)
        {
            foreach (var action in this.deleteActions)
            {
                //Reset the isSucceeded flag
                this.isSucceeded = this.isSucceeded.GetValueOrDefault(true) ? null : false;

                await action.Invoke();
            }
        }

        if (this.isSucceeded.GetValueOrDefault(false) && (changeType == ChangeType.Added || changeType == ChangeType.Modified))
            await UpdateLastReplicationDateAsync();
        else if(this.isSucceeded.GetValueOrDefault(false) && changeType == ChangeType.Deleted)
            await RemoveDeleteRowAsync();


        //Dispose the service provider at the end
        this.serviceProvider = null;
    }

    private async Task UpdateLastReplicationDateAsync()
    {
        this.entity.UpdateReplicationDate();

        var dbContext = (ShiftDbContext)this.serviceProvider.GetRequiredService(this.dbContextType);

        dbContext.Attach(entity);
        dbContext.Entry(entity).Property(nameof(ShiftEntity<object>.LastReplicationDate)).IsModified = true;
        await dbContext.SaveChangesWithoutTriggersAsync();
    }

    private async Task RemoveDeleteRowAsync()
    {
        var dbContext = (ShiftDbContext)this.serviceProvider.GetRequiredService(this.dbContextType);

        var query = dbContext.DeletedRowLogs.Where(x =>
            x.RowID == this.entity.ID &&
            x.ContainerName == this.replicateContainerId
        );

        if (this.partitionKeyDetails?.level1 is not null)
            query = query.Where(x => x.PartitionKeyLevelOneValue == this.partitionKeyDetails.Value.level1.Value.value &&
                x.PartitionKeyLevelOneType == this.partitionKeyDetails.Value.level1.Value.type);

        if (this.partitionKeyDetails?.level2 is not null)
            query = query.Where(x => x.PartitionKeyLevelTwoValue == this.partitionKeyDetails.Value.level2.Value.value &&
                                    x.PartitionKeyLevelTwoType == this.partitionKeyDetails.Value.level2!.Value.type);

        if (this.partitionKeyDetails?.level3 is not null)
            query = query.Where(x => x.PartitionKeyLevelThreeValue == this.partitionKeyDetails.Value.level3!.Value.value &&
                x.PartitionKeyLevelThreeType == this.partitionKeyDetails.Value.level3!.Value.type);

        var log = await query.SingleOrDefaultAsync();

        if (log is not null)
        {
            dbContext.DeletedRowLogs.Remove(log);
            await dbContext.SaveChangesWithoutTriggersAsync();
        }
    }
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
