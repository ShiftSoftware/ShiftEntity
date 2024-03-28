using AutoMapper;
using EntityFrameworkCore.Triggered.Extensions;
using FluentValidation.TestHelper;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.CosmosDbReplication.Exceptions;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.EFCore.Entities;
using System.Collections.Concurrent;
using System.ComponentModel.Design;
using System.Linq.Expressions;
using System.Net;

namespace ShiftSoftware.ShiftEntity.CosmosDbReplication.Services;

public class CosmosDBReplication
{
    private readonly IServiceProvider services;

    public CosmosDBReplication(IServiceProvider services)
    {
        this.services = services;
    }

    public CosmosDbReplicationOperation<DB, Entity> SetUp<DB, Entity>(string cosmosDbConnectionString, string cosmosDataBaseId,
        Func<IQueryable<Entity>, IQueryable<Entity>>? query = null)
        where DB : ShiftDbContext
        where Entity : ShiftEntity<Entity>
    {
        return new CosmosDbReplicationOperation<DB, Entity>(cosmosDbConnectionString, cosmosDataBaseId, services, query);
    }
}
public class CosmosDbReplicationOperation<DB, Entity>
    where DB : ShiftDbContext
    where Entity : ShiftEntity<Entity>
{
    private readonly string cosmosDbConnectionString;
    private readonly string cosmosDbDatabaseId;
    private readonly IServiceProvider services;
    private readonly Func<IQueryable<Entity>, IQueryable<Entity>>? query;

    public CosmosDbReplicationOperation(
        string cosmosDbConnectionString,
        string cosmosDbDatabaseId,
        IServiceProvider services,
        Func<IQueryable<Entity>, IQueryable<Entity>>? query)
    {
        this.cosmosDbConnectionString = cosmosDbConnectionString;
        this.cosmosDbDatabaseId = cosmosDbDatabaseId;
        this.services = services;
        this.query = query;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="CosmosDBItem"></typeparam>
    /// <param name="containerId"></param>
    /// <param name="mapping">If null, it use Auto Mapper for mapping</param>
    /// <returns></returns>
    public CosmosDbReferenceOperation<DB, Entity> Replicate<CosmosDBItem>(string containerId, Func<Entity, CosmosDBItem>? mapping = null)
    {
        CosmosDbReferenceOperation<DB, Entity> referenceOperations = new(this.cosmosDbConnectionString, this.cosmosDbDatabaseId,
            this.services, this.query);

        return referenceOperations.Replicate<CosmosDBItem>(containerId, mapping);
    }
}

public class CosmosDbReferenceOperation<DB, Entity> : IDisposable
    where DB : ShiftDbContext
    where Entity : ShiftEntity<Entity>
{
    private readonly string cosmosDbConnectionString;
    private readonly string cosmosDbDatabaseId;
    private readonly IMapper mapper;
    private readonly DB db;
    private readonly DbSet<Entity> dbSet;
    private readonly Func<IQueryable<Entity>, IQueryable<Entity>>? query;

    private IEnumerable<Entity> entities;
    private IEnumerable<DeletedRowLog> deletedRows;
    private List<string> cosmosContainerIds = new();

    private Database cosmosDatabase;

    private string replicationContainerId;

    List<Func<Task>> upsertActions = new();
    List<Func<Task>> deleteActions = new();
    ConcurrentDictionary<long, SuccessResponse> cosmosDeleteSuccesses = new();
    ConcurrentDictionary<long, SuccessResponse> cosmosUpsertSuccesses = new();

    public CosmosDbReferenceOperation(
        string cosmosDbConnectionString,
        string cosmosDbDatabaseId,
        IServiceProvider services,
        Func<IQueryable<Entity>, IQueryable<Entity>>? query)
    {
        this.cosmosDbConnectionString = cosmosDbConnectionString;
        this.cosmosDbDatabaseId = cosmosDbDatabaseId;
        this.mapper = services.GetRequiredService<IMapper>();
        this.db = services.GetRequiredService<DB>();
        this.dbSet = this.db.Set<Entity>();
        this.query = query;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="CosmosDBItem"></typeparam>
    /// <param name="containerId"></param>
    /// <param name="mapping">If null, it use Auto Mapper for mapping</param>
    /// <returns></returns>
    internal CosmosDbReferenceOperation<DB, Entity> Replicate<CosmosDBItem>(string containerId, Func<Entity, CosmosDBItem>? mapping = null)
    {
        this.replicationContainerId = containerId;
        this.cosmosContainerIds.Add(containerId);

        //Upsert fail replicated entities into cosmos container
        this.upsertActions.Add(async () =>
        {
            var container = this.cosmosDatabase.GetContainer(containerId);
            
            List<Task> cosmosTasks = new();

            foreach (var entity in this.entities)
            {
                CosmosDBItem item;
                if (mapping is not null)
                    item = mapping(entity);
                else
                    item = this.mapper.Map<CosmosDBItem>(entity);

                var entityId = entity.ID;

                cosmosTasks.Add(container.UpsertItemAsync<CosmosDBItem>(item)
                    .ContinueWith(x =>
                    {
                        this.cosmosUpsertSuccesses.AddOrUpdate(entityId, SuccessResponse.Create(x.IsCompletedSuccessfully),
                                                       (key, oldValue) => oldValue.Set(x.IsCompletedSuccessfully));
                    }));
            }

            await Task.WhenAll(cosmosTasks);
        });

        //Delete fail deleted rows from cosmos container
        this.deleteActions.Add(async () =>
        {
            var container = this.cosmosDatabase.GetContainer(containerId);

            List<Task> cosmosTasks = new();

            foreach (var deletedRow in this.deletedRows)
            {
                var key = Utility.GetPartitionKey(deletedRow);

                cosmosTasks.Add(container.DeleteItemAsync<CosmosDBItem>(deletedRow.RowID.ToString(), key)
                    .ContinueWith(x =>
                    {
                        CosmosException ex = null;

                        if (x.Exception != null)
                            foreach (var innerException in x.Exception.InnerExceptions)
                                if (innerException is CosmosException customException)
                                    ex = customException;

                        bool success = x.IsCompletedSuccessfully || ex?.StatusCode == HttpStatusCode.NotFound;

                        this.cosmosDeleteSuccesses.AddOrUpdate(deletedRow.ID, SuccessResponse.Create(success),
                                                                        (key, oldValue) => oldValue.Set(success));
                    }));
            }

            await Task.WhenAll(cosmosTasks);
        });

        return this;
    }

    public CosmosDbReferenceOperation<DB, Entity> UpdatePropertyReference<CosmosDBItemReference, DestinationContainer>(
        string containerId, Expression<Func<DestinationContainer, object>> destinationReferencePropertyExpression,
        Func<IQueryable<DestinationContainer>, Entity, IQueryable<DestinationContainer>> finder,
        Func<IQueryable<DestinationContainer>, DeletedRowLog, IQueryable<DestinationContainer>> deletedRowFinder,
        Func<Entity, CosmosDBItemReference>? mapping = null)
    {
        string propertyPath = Utility.GetPropertyFullPath(destinationReferencePropertyExpression); ;
        this.cosmosContainerIds.Add(containerId);

        //Update reference
        this.upsertActions.Add(async () =>
        {
            var container = this.cosmosDatabase.GetContainer(containerId);

            var containerReposne = await container.ReadContainerAsync();

            List<Task> cosmosTasks = new();

            foreach (var entity in this.entities)
            {
                var items = await GetItemsForReferenceUpdate(container, entity, finder);

                if (items is not null && items?.Count() > 0)
                {
                    foreach (var item in items)
                    {
                        CosmosDBItemReference propertyItem;
                        if (mapping is not null)
                            propertyItem = mapping(entity);
                        else
                            propertyItem = this.mapper.Map<CosmosDBItemReference>(entity);

                        var id = Convert.ToString(item.GetProperty("id"));
                        PartitionKey partitionKey = Utility.GetPartitionKey(containerReposne, item);

                        var entityId = entity.ID;

                        cosmosTasks.Add(container.PatchItemAsync<DestinationContainer>(id, partitionKey,
                            new[] { PatchOperation.Replace($"/{propertyPath}", propertyItem) })
                            .ContinueWith(x =>
                            {
                                this.cosmosUpsertSuccesses.AddOrUpdate(entityId, SuccessResponse.Create(x.IsCompletedSuccessfully),
                                                                        (key, oldValue) => oldValue.Set(x.IsCompletedSuccessfully));
                            }));
                    }
                }
                else
                {
                    this.cosmosUpsertSuccesses.AddOrUpdate(
                        entity.ID,
                        id => new SuccessResponse(), // This function is called if entity.ID does not exist in the dictionary
                        (id, oldValue) => oldValue.ResetToPreviousState() // This function is called if entity.ID exists in the dictionary
                    );
                }
            }

            await Task.WhenAll(cosmosTasks);
        });

        //Delete references
        this.deleteActions.Add(async () =>
        {
            var container = this.cosmosDatabase.GetContainer(containerId);

            var containerReposne = await container.ReadContainerAsync();

            List<Task> cosmosTasks = new();

            foreach (var row in this.deletedRows)
            {
                var items = await GetItemsForReferenceDelete(container, row, deletedRowFinder);

                if (items is not null && items?.Count() > 0) 
                {
                    foreach (var item in items)
                    {
                        var id = Convert.ToString(item.GetProperty("id"));
                        PartitionKey partitionKey = Utility.GetPartitionKey(containerReposne, item);

                        cosmosTasks.Add(container.PatchItemAsync<DestinationContainer>(id, partitionKey,
                            new[] { PatchOperation.Remove($"/{propertyPath}") })
                            .ContinueWith(x =>
                            {
                                CosmosException ex = null;

                                if (x.Exception != null)
                                    foreach (var innerException in x.Exception.InnerExceptions)
                                        if (innerException is CosmosException customException)
                                            ex = customException;

                                bool success = x.IsCompletedSuccessfully || ex?.StatusCode == HttpStatusCode.NotFound;

                                this.cosmosDeleteSuccesses.AddOrUpdate(row.ID, SuccessResponse.Create(success),
                                                                    (key, oldValue) => oldValue.Set(success));
                            }));
                    }
                }
                else
                {
                    this.cosmosDeleteSuccesses.AddOrUpdate(
                            row.ID,
                            id => new SuccessResponse(), // This function is called if entity.ID does not exist in the dictionary
                            (id, oldValue) => oldValue.ResetToPreviousState() // This function is called if entity.ID exists in the dictionary
                        );
                }
            }

            await Task.WhenAll(cosmosTasks);
        });

        return this;
    }

    public CosmosDbReferenceOperation<DB, Entity> UpdateReference<CosmosDBItem>(string containerId,
        Func<IQueryable<CosmosDBItem>, Entity, IQueryable<CosmosDBItem>> finder,
        Func<IQueryable<CosmosDBItem>, DeletedRowLog, IQueryable<CosmosDBItem>> deletedRowFinder,
        Func<Entity, CosmosDBItem, CosmosDBItem>? mapping = null)
    {
        this.cosmosContainerIds.Add(containerId);

        this.upsertActions.Add(async () =>
        {
            var container = this.cosmosDatabase.GetContainer(containerId);

            List<Task> cosmosTasks = new();

            foreach (var entity in this.entities)
            {
                var items = await GetItemsForReferenceUpdate(container, entity, finder);

                if (items is not null && items?.Count() > 0)
                {
                    foreach (var item in items)
                    {
                        CosmosDBItem tempItems = item;
                        if (mapping is null)
                            this.mapper.Map(entity, tempItems);
                        else
                            tempItems = mapping(entity, item);


                        cosmosTasks.Add(container.UpsertItemAsync<CosmosDBItem>(tempItems)
                            .ContinueWith(x =>
                            {
                                this.cosmosUpsertSuccesses.AddOrUpdate(entity.ID, SuccessResponse.Create(x.IsCompletedSuccessfully),
                                                                            (key, oldValue) => oldValue.Set(x.IsCompletedSuccessfully));
                            }));
                    }
                }
                else
                {
                    this.cosmosUpsertSuccesses.AddOrUpdate(
                            entity.ID,
                            id => new SuccessResponse(), // This function is called if entity.ID does not exist in the dictionary
                            (id, oldValue) => oldValue.ResetToPreviousState() // This function is called if entity.ID exists in the dictionary
                        );
                }
            }

            await Task.WhenAll(cosmosTasks);
        });

        this.deleteActions.Add(async () =>
        {
            var container = this.cosmosDatabase.GetContainer(containerId);
            var containerResponse = await container.ReadContainerAsync();

            List<Task> cosmosTasks = new();

            foreach (var row in this.deletedRows)
            {
                var items = await GetItemsForReferenceDelete(container, row, deletedRowFinder);
                if(items is not null && items?.Count() > 0)
                {
                    foreach (var item in items)
                    {
                        var key = Utility.GetPartitionKey(containerResponse, item);

                        cosmosTasks.Add(container.DeleteItemAsync<CosmosDBItem>(row.RowID.ToString(), key)
                        .ContinueWith(x =>
                        {
                            CosmosException ex = null;

                            if (x.Exception != null)
                                foreach (var innerException in x.Exception.InnerExceptions)
                                    if (innerException is CosmosException customException)
                                        ex = customException;

                            bool success = x.IsCompletedSuccessfully || ex?.StatusCode == HttpStatusCode.NotFound;

                            this.cosmosDeleteSuccesses.AddOrUpdate(row.ID, SuccessResponse.Create(success),
                                                                (key, oldValue) => oldValue.Set(success));
                        }));
                    }
                }
                else
                {
                    this.cosmosDeleteSuccesses.AddOrUpdate(
                            row.ID,
                            id => new SuccessResponse(), // This function is called if entity.ID does not exist in the dictionary
                            (id, oldValue) => oldValue.ResetToPreviousState() // This function is called if entity.ID exists in the dictionary
                        );
                }
            }

            await Task.WhenAll(cosmosTasks);
        });

        return this;
    }

    private async Task<IEnumerable<CosmosDBItem>> GetItemsForReferenceUpdate<CosmosDBItem>(Microsoft.Azure.Cosmos.Container container, Entity entity,
        Func<IQueryable<CosmosDBItem>, Entity, IQueryable<CosmosDBItem>> finder)
    {
        var query = container.GetItemLinqQueryable<CosmosDBItem>().AsQueryable();

        query = finder(query, entity);

        var feedIterator = query.ToFeedIterator<CosmosDBItem>();

        List<CosmosDBItem> items = new();

        while (feedIterator.HasMoreResults)
        {
            items.AddRange(await feedIterator.ReadNextAsync());
        }

        return items;
    }

    private async Task<IEnumerable<CosmosDBItem>> GetItemsForReferenceDelete<CosmosDBItem>(Microsoft.Azure.Cosmos.Container container, 
        DeletedRowLog row,
        Func<IQueryable<CosmosDBItem>, DeletedRowLog, IQueryable<CosmosDBItem>> deletedRowFinder)
    {
        var query = container.GetItemLinqQueryable<CosmosDBItem>().AsQueryable();

        var extendedQuery = deletedRowFinder(query, row);

        var feedIterator = extendedQuery.ToFeedIterator<CosmosDBItem>();

        List<CosmosDBItem> items = new();

        while (feedIterator.HasMoreResults)
        {
            items.AddRange(await feedIterator.ReadNextAsync());
        }

        return items;
    }

    public async Task RunAsync(bool removeDeleteRowLog = true, bool updateAll = false)
    {
        //Return fail replicated entities
        var queryable = this.dbSet.AsQueryable();

        if (!updateAll)
            queryable = queryable.Where(x => x.LastReplicationDate < x.LastSaveDate || !x.LastReplicationDate.HasValue);

        if (this.query is not null)
            queryable = this.query(queryable);

        this.entities = await queryable.ToArrayAsync();

        //Return delete rows that failed to replicate
        var deleteRowsQueryalbe = db.DeletedRowLogs.Where(x => this.replicationContainerId == x.ContainerName);
        if(!updateAll)
            deleteRowsQueryalbe = deleteRowsQueryalbe.Where(x => !x.LastReplicationDate.HasValue);

        this.deletedRows = await deleteRowsQueryalbe.ToArrayAsync();

        //If there is no entities or deleted rows to replicate, terminate the process
        if ((this.entities is null || this.entities?.Count() == 0) && (this.deletedRows is null || this.deletedRows?.Count() == 0))
            return;

        //Prepare the lists of the container that used, to the connection
        List<(string databaseId, string continerId)> containers = new();
        foreach (var containerId in this.cosmosContainerIds)
            containers.Add((this.cosmosDbDatabaseId, containerId));

        //Connect to the cosmos selected database and containers
        using var client = await CosmosClient.CreateAndInitializeAsync(this.cosmosDbConnectionString, containers, new CosmosClientOptions()
        {
            AllowBulkExecution = true
        });
        this.cosmosDatabase = client.GetDatabase(this.cosmosDbDatabaseId);


        foreach (var action in this.deleteActions)
        {
            this.ResetDeleteSuccess();
            await action.Invoke();
        }

        foreach (var action in this.upsertActions)
        {
            this.ResetUpsertSuccess();
            await action.Invoke();
        }

        UpdateLastReplicationDatesAsync();

        if (removeDeleteRowLog)
            DeleteReplicatedDeletedRowLogs();
        else
            UpdateReplicatedDeletedRowLogs();

        await this.db.SaveChangesWithoutTriggersAsync();

        this.Dispose();
    }

    private void UpdateLastReplicationDatesAsync()
    {
        foreach (var entity in this.entities)
            if (this.cosmosUpsertSuccesses.GetOrAdd(entity.ID, new SuccessResponse()).Get())
                entity.UpdateReplicationDate();
    }

    private void DeleteReplicatedDeletedRowLogs()
    {
        foreach (var row in this.deletedRows)
            if (this.cosmosDeleteSuccesses.GetOrAdd(row.ID, new SuccessResponse()).Get())
                db.DeletedRowLogs.Remove(row);
    }

    private void UpdateReplicatedDeletedRowLogs()
    {
        foreach (var row in this.deletedRows)
            if (this.cosmosDeleteSuccesses.GetOrAdd(row.ID, new SuccessResponse()).Get())
                row.LastReplicationDate = DateTime.UtcNow;
    }

    private void ResetUpsertSuccess()
    {
        this.cosmosUpsertSuccesses = new ConcurrentDictionary<long, SuccessResponse>(this.cosmosUpsertSuccesses
            .ToDictionary(x => x.Key, x => x.Value?.Reset() ?? new SuccessResponse()));
    }

    private void ResetDeleteSuccess()
    {
        this.cosmosDeleteSuccesses = new ConcurrentDictionary<long, SuccessResponse>(this.cosmosDeleteSuccesses
                    .ToDictionary(x => x.Key, x => x.Value?.Reset() ?? new SuccessResponse()));
    }

    public void Dispose()
    {
        this.entities = null!;
        this.deletedRows = null!;

        this.replicationContainerId = null!;

        this.upsertActions = null!;
        this.deleteActions = null!;
        this.cosmosDeleteSuccesses = null!;
        this.cosmosUpsertSuccesses = null!;

        this.cosmosDatabase = null!;
    }
}

class SuccessResponse
{
    public bool? Previous { get; set; }
    public bool? Current { get; set; }

    public static SuccessResponse Create(bool value)
    {
        return new SuccessResponse { Previous = null, Current = value };
    }

    public SuccessResponse Set(bool value)
    {
        this.Current = this.Previous.GetValueOrDefault(true) && this.Current.GetValueOrDefault(true) && value;

        return this;
    }

    public bool Get()
    {
        return this.Current.GetValueOrDefault(false);
    }

    public SuccessResponse ResetToPreviousState()
    {
        if (!this.Current.HasValue)
            this.Current = this.Previous;
        else
            this.Current = this.Previous.GetValueOrDefault(true) && this.Current.Value;

        return this;
    }

    public SuccessResponse Reset()
    {
        this.Previous = this.Current;
        this.Current = null;

        return this;
    }
}
