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
using ShiftSoftware.ShiftEntity.Model.Replication;
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
        where Entity : ShiftEntity<Entity>, IShiftEntityReplication
    {
        return new CosmosDbReplicationOperation<DB, Entity>(cosmosDbConnectionString, cosmosDataBaseId, services, query);
    }

    public CosmosDbReplicationOperation<DB, Entity> SetUp<DB, Entity>(CosmosClient client, string cosmosDataBaseId,
        Func<IQueryable<Entity>, IQueryable<Entity>>? query = null)
        where DB : ShiftDbContext
        where Entity : ShiftEntity<Entity>, IShiftEntityReplication
    {
        return new CosmosDbReplicationOperation<DB, Entity>(client, cosmosDataBaseId, services, query);
    }
}
public class CosmosDbReplicationOperation<DB, Entity>
    where DB : ShiftDbContext
    where Entity : ShiftEntity<Entity>, IShiftEntityReplication
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
    where Entity : ShiftEntity<Entity>, IShiftEntityReplication
{
    private readonly string cosmosDbConnectionString;
    private CosmosClient client;
    private readonly string cosmosDbDatabaseId;
    private readonly IMapper mapper;
    private readonly DB db;
    private readonly DbSet<Entity> dbSet;
    private readonly Func<IQueryable<Entity>, IQueryable<Entity>>? query;

    private IEnumerable<Entity> entities;
    private List<string> cosmosContainerIds = new();

    private Database cosmosDatabase;

    List<Func<Task>> upsertActions = new();
    ConcurrentDictionary<long, SuccessResponse> cosmosUpsertSuccesses = new();

    //Keyed by entity ID, holds the serialized LastReplicationStamp (document id + partition-key levels) computed
    //during this sync. Written back to each entity's LastReplicationStamp column after a successful upsert so
    //the NEXT sync — trigger or catch-up — can detect an id or partition-key change and delete the stale Cosmos
    //document under the OLD id + key before upserting the new one.
    Dictionary<long, string> pendingStamps = new();

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
        this.cosmosContainerIds.Add(containerId);

        //Upsert fail replicated entities into cosmos container
        this.upsertActions.Add(async () =>
        {
            var container = this.cosmosDatabase.GetContainer(containerId);
            var containerResponse = await container.ReadContainerAsync();

            List<Task> cosmosTasks = new();

            foreach (var entity in this.entities)
            {
                //One bad row (a mapping that throws, a stamp that defeats even Deserialize's validation) must fail
                //THAT row — marked unsuccessful below so it stays dirty and is retried — never abort the whole
                //catch-up run for every other entity.
                try
                {
                    CosmosDBItem newItem;
                    if (mapping is not null)
                        newItem = mapping(entity);
                    else
                        newItem = this.mapper.Map<CosmosDBItem>(entity);

                    var entityId = entity.ID;

                    //Detect an id or partition-key change since the last successful sync and remove the stale Cosmos
                    //document under the OLD id + key before upserting under the new one (Cosmos can't mutate a document's
                    //id or PK, so a naive upsert would orphan the old doc). The mapping only ever runs on the fully
                    //loaded current entity, so navigation-derived keys/fields are always populated.
                    string? deleteId = null;
                    PartitionKey? oldPartitionKey = null;

                    var newStamp = Utility.BuildStamp(containerResponse, newItem!);
                    var oldStamp = LastReplicationStamp.Deserialize(entity.LastReplicationStamp);

                    if (oldStamp is not null && newStamp.DiffersFrom(oldStamp))
                    {
                        deleteId = oldStamp.Id;
                        oldPartitionKey = oldStamp.BuildPartitionKey();
                    }

                    this.pendingStamps[entityId] = newStamp.Serialize();

                    cosmosTasks.Add(UpsertWithPartitionKeyChangeHandlingAsync(container, newItem, entityId, deleteId, oldPartitionKey));
                }
                catch
                {
                    this.cosmosUpsertSuccesses.AddOrUpdate(entity.ID, SuccessResponse.Create(false),
                        (key, oldValue) => oldValue.Set(false));
                }
            }

            await Task.WhenAll(cosmosTasks);

            async Task UpsertWithPartitionKeyChangeHandlingAsync(Microsoft.Azure.Cosmos.Container c, CosmosDBItem item, long id, string? deleteId, PartitionKey? deletePk)
            {
                bool success = true;

                if (deletePk.HasValue && deleteId is not null)
                {
                    try
                    {
                        await c.DeleteItemAsync<CosmosDBItem>(deleteId, deletePk.Value);
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

        return this;
    }

    public CosmosDbReferenceOperation<DB, Entity> UpdatePropertyReference<CosmosDBItemReference, DestinationContainer>(
        string containerId, Expression<Func<DestinationContainer, object>> destinationReferencePropertyExpression,
        Func<IQueryable<DestinationContainer>, Entity, IQueryable<DestinationContainer>> finder,
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

        return this;
    }

    public CosmosDbReferenceOperation<DB, Entity> UpdateReference<CosmosDBItem>(string containerId,
        Func<IQueryable<CosmosDBItem>, Entity, IQueryable<CosmosDBItem>> finder,
        Func<Entity, CosmosDBItem, CosmosDBItem>? mapping = null)
    {
        this.cosmosContainerIds.Add(containerId);

        this.upsertActions.Add(async () =>
        {
            var container = this.cosmosDatabase.GetContainer(containerId);
            var containerResponse = await container.ReadContainerAsync();

            //Several source entities can reference the SAME destination document (e.g. multiple tags embedded in
            //one occupation). Each item is a full read-modify-write upsert, so writing per (entity, item) pair lets
            //those writes overwrite each other with stale copies (lost update). Instead, group the found documents
            //by their Cosmos identity and merge EVERY contributing entity's update onto a single document instance,
            //then upsert each document ONCE. Writes stay fully parallel (one per distinct document), so bulk
            //execution is preserved.
            var groupedByDocument = new Dictionary<string, (CosmosDBItem document, List<Entity> contributors)>();

            foreach (var entity in this.entities)
            {
                var items = await GetItemsForReferenceUpdate(container, entity, finder);

                if (items is not null && items.Any())
                {
                    foreach (var item in items)
                    {
                        //A Cosmos document is identified by id + partition key, NOT id alone: the same id can exist
                        //in different logical partitions (e.g. an Occupation and an IncomeLevel both id "1" under
                        //different ItemTypes). Key the group by both so distinct documents are never merged together.
                        var id = Convert.ToString(item.GetProperty("id"));
                        var partitionKey = Utility.GetPartitionKey(containerResponse, item!);
                        var documentKey = $"{partitionKey}|{id}";

                        if (groupedByDocument.TryGetValue(documentKey, out var existing))
                            existing.contributors.Add(entity);
                        else
                            groupedByDocument[documentKey] = (item, new List<Entity> { entity });
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

            //One upsert per distinct destination document, all queued together for bulk execution.
            List<Task> cosmosTasks = new();

            foreach (var entry in groupedByDocument.Values)
            {
                CosmosDBItem mergedDocument = entry.document;

                foreach (var entity in entry.contributors)
                {
                    if (mapping is null)
                        this.mapper.Map(entity, mergedDocument);
                    else
                        mergedDocument = mapping(entity, mergedDocument);
                }

                //A successful (or failed) write counts for EVERY source entity that merged into the document.
                var contributors = entry.contributors;

                cosmosTasks.Add(container.UpsertItemAsync<CosmosDBItem>(mergedDocument)
                    .ContinueWith(x =>
                    {
                        foreach (var entity in contributors)
                            this.cosmosUpsertSuccesses.AddOrUpdate(entity.ID, SuccessResponse.Create(x.IsCompletedSuccessfully),
                                                                        (key, oldValue) => oldValue.Set(x.IsCompletedSuccessfully));
                    }));
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

    public async Task RunAsync(bool updateAll = false)
    {
        //Return fail replicated entities
        var queryable = this.dbSet.AsQueryable();

        if (!updateAll)
            //Dirty = the replicated-version watermark is behind the row's save date (or absent: never replicated).
            //LastReplicationDate is that watermark — the save date of the replicated version, not a run timestamp.
            queryable = queryable.Where(x => x.LastReplicationDate < x.LastSaveDate || !x.LastReplicationDate.HasValue);

        if (this.query is not null)
            queryable = this.query(queryable);

        this.entities = await queryable.ToArrayAsync();

        //If there is no entities to replicate, terminate the process
        if (this.entities is null || this.entities?.Count() == 0)
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


        foreach (var action in this.upsertActions)
        {
            this.ResetUpsertSuccess();
            await action.Invoke();
        }

        ApplyReplicationBookkeeping();

        //A replication-bookkeeping save: it must write exactly the replication columns set above. No triggers, and
        //no audit backfill — these entities were loaded fresh in this scope, so without the suppression the audit
        //sweep would treat this infrastructure save as a real edit.
        using (this.db.SuppressAuditStamping())
            await this.db.SaveChangesWithoutTriggersAsync();

        this.Dispose();
    }

    private void ApplyReplicationBookkeeping()
    {
        //Success implies a pending stamp exists: the upsert action records it before queueing the Cosmos call,
        //and rows whose mapping or stamp computation threw are marked unsuccessful. The TryGetValue keeps the
        //unreachable missing-stamp case on the safe side — the row stays dirty and is retried, rather than being
        //marked clean with stale coordinates.
        foreach (var entity in this.entities)
            if (this.cosmosUpsertSuccesses.GetOrAdd(entity.ID, new SuccessResponse()).Get() &&
                this.pendingStamps.TryGetValue(entity.ID, out var stamp))
                entity.MarkReplicated(stamp);
    }

    private void ResetUpsertSuccess()
    {
        this.cosmosUpsertSuccesses = new ConcurrentDictionary<long, SuccessResponse>(this.cosmosUpsertSuccesses
            .ToDictionary(x => x.Key, x => x.Value?.Reset() ?? new SuccessResponse()));
    }

    public void Dispose()
    {
        this.entities = null!;

        this.upsertActions = null!;
        this.cosmosUpsertSuccesses = null!;

        this.pendingStamps = null!;

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
