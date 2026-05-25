using AutoMapper;
using EntityFrameworkCore.Triggered.Extensions;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.CosmosDbReplication.Exceptions;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.EFCore.Entities;
using System.Collections.Concurrent;
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

    public CosmosDbReplicationOperation<DB, Entity> SetUp<DB, Entity>(CosmosClient client, string cosmosDataBaseId,
        Func<IQueryable<Entity>, IQueryable<Entity>>? query = null)
        where DB : ShiftDbContext
        where Entity : ShiftEntity<Entity>
    {
        return new CosmosDbReplicationOperation<DB, Entity>(client, cosmosDataBaseId, services, query);
    }
}
public class CosmosDbReplicationOperation<DB, Entity>
    where DB : ShiftDbContext
    where Entity : ShiftEntity<Entity>
{
    private readonly string? cosmosDbConnectionString;
    private readonly string cosmosDbDatabaseId;
    private readonly IServiceProvider services;
    private readonly Func<IQueryable<Entity>, IQueryable<Entity>>? query;
    private readonly CosmosClient? client;

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

    public CosmosDbReplicationOperation(
        CosmosClient client,
        string cosmosDbDatabaseId,
        IServiceProvider services,
        Func<IQueryable<Entity>, IQueryable<Entity>>? query)
    {
        this.client = client;
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
        CosmosDbReferenceOperation<DB, Entity> referenceOperations;

        if (this.client is null)
            referenceOperations = new(this.cosmosDbConnectionString!, this.cosmosDbDatabaseId, this.services, this.query);
        else
            referenceOperations = new(this.client, this.cosmosDbDatabaseId, this.services, this.query);

        return referenceOperations.Replicate<CosmosDBItem>(containerId, mapping);
    }
}

public class CosmosDbReferenceOperation<DB, Entity> : IDisposable
    where DB : ShiftDbContext
    where Entity : ShiftEntity<Entity>
{
    private readonly string cosmosDbConnectionString;
    private CosmosClient client;
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

    //Populated only when Entity is mapped to a SQL Server temporal table.
    //Keyed by entity ID, holds the historical row that was current at the time replication last succeeded
    //(matched by PeriodStart <= LastReplicationDate < PeriodEnd) so we can detect a partition-key change
    //and delete the stale Cosmos document under the OLD partition key before upserting the new one.
    Dictionary<long, Entity> previouslySyncedEntities = new();

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

    public CosmosDbReferenceOperation(
        CosmosClient client,
        string cosmosDbDatabaseId,
        IServiceProvider services,
        Func<IQueryable<Entity>, IQueryable<Entity>>? query)
    {
        this.client = client;
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
            var containerResponse = await container.ReadContainerAsync();

            List<Task> cosmosTasks = new();

            foreach (var entity in this.entities)
            {
                CosmosDBItem newItem;
                if (mapping is not null)
                    newItem = mapping(entity);
                else
                    newItem = this.mapper.Map<CosmosDBItem>(entity);

                var entityId = entity.ID;

                //If this entity is on a temporal table and we found the version that was current at the last
                //successful sync, detect a partition-key change and remove the stale Cosmos document under the
                //OLD partition key before upserting under the new one. Cosmos cannot mutate a document's PK,
                //so a naive upsert would leave the old doc orphaned.
                PartitionKey? oldPartitionKey = null;
                if (this.previouslySyncedEntities.TryGetValue(entityId, out var previousEntity))
                {
                    CosmosDBItem oldItem;
                    if (mapping is not null)
                        oldItem = mapping(previousEntity);
                    else
                        oldItem = this.mapper.Map<CosmosDBItem>(previousEntity);

                    var newPk = Utility.GetPartitionKey(containerResponse, newItem!);
                    var oldPk = Utility.GetPartitionKey(containerResponse, oldItem!);
                    if (!newPk.Equals(oldPk))
                        oldPartitionKey = oldPk;
                }

                cosmosTasks.Add(UpsertWithPartitionKeyChangeHandlingAsync(container, newItem, entityId, oldPartitionKey));
            }

            await Task.WhenAll(cosmosTasks);

            async Task UpsertWithPartitionKeyChangeHandlingAsync(Microsoft.Azure.Cosmos.Container c, CosmosDBItem item, long id, PartitionKey? deletePk)
            {
                bool success = true;

                if (deletePk.HasValue)
                {
                    var idString = Convert.ToString(item!.GetProperty("id"));
                    try
                    {
                        await c.DeleteItemAsync<CosmosDBItem>(idString, deletePk.Value);
                    }
                    catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                    {
                        //already gone; treat as success and proceed to upsert
                    }
                    catch
                    {
                        success = false;
                    }
                }

                if (success)
                {
                    try
                    {
                        await c.UpsertItemAsync<CosmosDBItem>(item);
                    }
                    catch
                    {
                        success = false;
                    }
                }

                this.cosmosUpsertSuccesses.AddOrUpdate(id, SuccessResponse.Create(success),
                    (key, oldValue) => oldValue.Set(success));
            }
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

        await LoadPreviouslySyncedEntitiesAsync();

        //Return delete rows that failed to replicate
        var deleteRowsQueryalbe = db.DeletedRowLogs.Where(x => this.replicationContainerId == x.ContainerName);
        if(!updateAll)
            deleteRowsQueryalbe = deleteRowsQueryalbe.Where(x => !x.LastReplicationDate.HasValue);

        this.deletedRows = await deleteRowsQueryalbe.ToArrayAsync();

        //If there is no entities or deleted rows to replicate, terminate the process
        if ((this.entities is null || this.entities?.Count() == 0) && (this.deletedRows is null || this.deletedRows?.Count() == 0))
            return;

        if(this.client is null)
        {
            //Prepare the lists of the container that used, to the connection
            List<(string databaseId, string continerId)> containers = new();
            foreach (var containerId in this.cosmosContainerIds)
                containers.Add((this.cosmosDbDatabaseId, containerId));

            //Connect to the cosmos selected database and containers
            this.client = await CosmosClient.CreateAndInitializeAsync(this.cosmosDbConnectionString, containers);
        }

        //Set cosmos client options
        this.client.ClientOptions.AllowBulkExecution = true;

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

    private async Task LoadPreviouslySyncedEntitiesAsync()
    {
        if (this.entities is null)
            return;

        //IsTemporal() works on the runtime read-optimized model.
        var designTimeModel = this.db.GetService<IDesignTimeModel>().Model;
        var entityType = designTimeModel.FindEntityType(typeof(Entity));
        if (entityType is null || !entityType.IsTemporal())
            return;

        var idsToLookup = this.entities
            .Where(e => e.LastReplicationDate.HasValue)
            .Select(e => e.ID)
            .Distinct()
            .ToArray();

        if (idsToLookup.Length == 0)
            return;

        //Find the previously-synced version by matching the LastSaveDate column on the history row to the entity's
        //LastReplicationDate. UpdateReplicationDate() sets LRD = LastSaveDate, so the row we synced still carries the
        //exact same LastSaveDate value in the temporal history. Both columns are written from .NET (same clock), so
        //the match is robust against the SQL/.NET clock skew that breaks comparisons against PeriodStart/PeriodEnd
        //(SQL-clock managed). The explicit `join` form keeps it a single INNER JOIN — no correlated subquery — and
        //SQL Server can index on LastSaveDate for the temporal table if needed.
        var matched = await (
            from current in this.db.Set<Entity>().AsNoTracking().IgnoreQueryFilters()
            join history in this.db.Set<Entity>().TemporalAll().AsNoTracking().IgnoreQueryFilters()
                on current.ID equals history.ID
            where idsToLookup.Contains(current.ID)
                && current.LastReplicationDate.HasValue
                && history.LastSaveDate == current.LastReplicationDate!.Value
            select new { ID = current.ID, History = history }
        ).ToListAsync();

        //Multiple history rows can share the same LastSaveDate (e.g. the framework's own LRD-update on the synced row
        //creates a new history row without changing LastSaveDate). They all carry the same business data, so picking
        //any one of them yields the same partition-key comparison result.
        foreach (var group in matched.GroupBy(x => x.ID))
            this.previouslySyncedEntities[group.Key] = group.First().History;
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

        this.previouslySyncedEntities = null!;

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
