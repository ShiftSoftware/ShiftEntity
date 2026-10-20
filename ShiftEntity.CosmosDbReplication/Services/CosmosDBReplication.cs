using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.CosmosDbReplication.Exceptions;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.EFCore.Entities;
using ShiftSoftware.ShiftEntity.EFCore.Replication;
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
    /// <param name="mapping">
    /// Projects the entity into the Cosmos document. Optional: when omitted, the document is mapped through the
    /// host's registered ShiftMapper mapper (<see cref="ShiftMapper.IMapper"/>), which must declare
    /// <c>CreateMap&lt;Entity, CosmosDBItem&gt;()</c>. No registered mapper, or none declaring the pair, throws out of
    /// <see cref="CosmosDbReferenceOperation{DB, Entity}.RunAsync"/> before any row is touched.
    /// </param>
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

    /// <summary>
    /// Replicates the selected roots' <typeparamref name="CosmosDBItem"/>, built from each root loaded with its
    /// replication tree (<c>HasReplicationTree</c>): the catch-up counterpart of the save side's <c>ReplicateTree</c>.
    /// The roots are loaded the way the save side loads them, with the same includes, ignoring query filters and without
    /// tracking, so a backfilled document is the one a save writes. The tree document is the operation's only document.
    /// </summary>
    /// <param name="mapping">
    /// Builds the document from the loaded root. Optional: when omitted, the document is mapped through the host's
    /// registered ShiftMapper mapper, which must declare <c>CreateMap&lt;Entity, CosmosDBItem&gt;()</c>.
    /// </param>
    public CosmosDbReferenceOperation<DB, Entity> ReplicateTree<CosmosDBItem>(string containerId, Func<Entity, CosmosDBItem>? mapping = null)
    {
        CosmosDbReferenceOperation<DB, Entity> referenceOperations;

        if (this.client is null)
            referenceOperations = new(this.cosmosDbConnectionString!, this.cosmosDbDatabaseId, this.services, this.query);
        else
            referenceOperations = new(this.client, this.cosmosDbDatabaseId, this.services, this.query);

        return referenceOperations.ReplicateTree<CosmosDBItem>(containerId, mapping);
    }
}

public class CosmosDbReferenceOperation<DB, Entity> : IDisposable
    where DB : ShiftDbContext
    where Entity : ShiftEntity<Entity>, IShiftEntityReplication
{
    private readonly string cosmosDbConnectionString;
    private CosmosClient client;
    private readonly string cosmosDbDatabaseId;
    private readonly IServiceProvider services;
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

    //Why each failed row failed, for the run's report: one entry per row and container that failed.
    ConcurrentQueue<CosmosDbReplicationFailure> failures = new();

    public CosmosDbReferenceOperation(
        string cosmosDbConnectionString,
        string cosmosDbDatabaseId,
        IServiceProvider services,
        Func<IQueryable<Entity>, IQueryable<Entity>>? query)
    {
        this.cosmosDbConnectionString = cosmosDbConnectionString;
        this.cosmosDbDatabaseId = cosmosDbDatabaseId;
        this.services = services;
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
        this.services = services;
        this.db = services.GetRequiredService<DB>();
        this.dbSet = this.db.Set<Entity>();
        this.query = query;
    }

    /// <summary>
    ///
    /// </summary>
    /// <typeparam name="CosmosDBItem"></typeparam>
    /// <param name="containerId"></param>
    /// <param name="mapping">
    /// Projects the entity into the Cosmos document. Optional: when omitted, the document is mapped through the
    /// host's registered ShiftMapper mapper (<see cref="ShiftMapper.IMapper"/>).
    /// </param>
    /// <returns></returns>
    internal CosmosDbReferenceOperation<DB, Entity> Replicate<CosmosDBItem>(string containerId, Func<Entity, CosmosDBItem>? mapping = null) =>
        this.AddDocument(containerId, mapping, $"Replicate<{typeof(CosmosDBItem).Name}>(\"{containerId}\")");

    /// <summary>
    /// Replicates <typeparamref name="CosmosDBItem"/> built from each selected root loaded with its replication tree. It
    /// is the row's only document, so its stamp is the row's.
    /// </summary>
    internal CosmosDbReferenceOperation<DB, Entity> ReplicateTree<CosmosDBItem>(string containerId, Func<Entity, CosmosDBItem>? mapping = null)
    {
        this.fromTree = true;
        return this.AddDocument(containerId, mapping, $"ReplicateTree<{typeof(CosmosDBItem).Name}>(\"{containerId}\")");
    }

    //Whether the roots load with their replication tree.
    private bool fromTree;

    private CosmosDbReferenceOperation<DB, Entity> AddDocument<CosmosDBItem>(string containerId, Func<Entity, CosmosDBItem>? mapping,
        string operation)
    {
        this.cosmosContainerIds.Add(containerId);

        //Upsert fail replicated entities into cosmos container
        this.upsertActions.Add(async () =>
        {
            //No delegate → the host's ShiftMapper mapper. Resolved ONCE, above the loop, so a missing registration
            //(or a mapper that does not declare this pair) throws out of the run. Resolving it at the use site
            //below would put it INSIDE the per-row catch, which swallows and marks the row unsuccessful — turning
            //"the host forgot AddShiftMapper" into a sweep that reports success while leaving every row permanently
            //dirty under a clean-looking watermark. That is exactly how the old AutoMapper fallback failed.
            var map = mapping ?? ReplicationMapper.ResolveCreate<Entity, CosmosDBItem>(this.services, operation);

            if (await this.ReadContainerAsync(containerId) is not { } read)
                return;

            var (container, containerResponse) = read;
            List<Task> cosmosTasks = new();

            foreach (var entity in this.entities)
            {
                //One bad row (a mapping that throws, a stamp that defeats even Deserialize's validation) must fail
                //THAT row — marked unsuccessful below so it stays dirty and is retried — never abort the whole
                //catch-up run for every other entity.
                try
                {
                    CosmosDBItem newItem = map(entity);

                    var entityId = entity.ID;

                    //Detect an id or partition-key change since the last successful sync and remove the stale Cosmos
                    //document under the OLD id + key before upserting under the new one (Cosmos can't mutate a document's
                    //id or PK, so a naive upsert would orphan the old doc). The mapping only ever runs on the fully
                    //loaded current entity, so navigation-derived keys/fields are always populated. A stamp written
                    //for another container locates no document in this one (see CanBeIn), so nothing is deleted for it.
                    string? deleteId = null;
                    PartitionKey? oldPartitionKey = null;

                    var newStamp = Utility.BuildStamp(containerResponse, newItem!);
                    var oldStamp = LastReplicationStamp.Deserialize(entity.LastReplicationStamp);

                    if (oldStamp is not null && newStamp.DiffersFrom(oldStamp) && oldStamp.CanBeIn(containerResponse))
                    {
                        deleteId = oldStamp.Id;
                        oldPartitionKey = oldStamp.BuildPartitionKey();
                    }

                    this.pendingStamps[entityId] = newStamp.Serialize();

                    cosmosTasks.Add(UpsertWithPartitionKeyChangeHandlingAsync(container, newItem, entityId, deleteId, oldPartitionKey));
                }
                catch (Exception ex)
                {
                    this.RecordFailure(entity.ID, containerId, ex);
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
                    catch (Exception ex)
                    {
                        success = false;
                        this.RecordFailure(id, containerId, ex);
                    }
                }

                if (success)
                {
                    try
                    {
                        await c.UpsertItemAsync<CosmosDBItem>(item);
                    }
                    catch (Exception ex)
                    {
                        success = false;
                        this.RecordFailure(id, containerId, ex);
                    }
                }

                this.cosmosUpsertSuccesses.AddOrUpdate(id, SuccessResponse.Create(success),
                    (key, oldValue) => oldValue.Set(success));
            }
        });

        return this;
    }

    /// <param name="mapping">
    /// Builds the embedded reference document from the entity. Optional: when omitted, it is mapped through the
    /// host's registered ShiftMapper mapper (<see cref="ShiftMapper.IMapper"/>), which must declare
    /// <c>CreateMap&lt;Entity, CosmosDBItemReference&gt;()</c>.
    /// </param>
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
            //Resolved once, above the loop — see Replicate for why it must never move inside it.
            var map = mapping ?? ReplicationMapper.ResolveCreate<Entity, CosmosDBItemReference>(this.services,
                $"UpdatePropertyReference<{typeof(CosmosDBItemReference).Name}, {typeof(DestinationContainer).Name}>(\"{containerId}\")");

            if (await this.ReadContainerAsync(containerId) is not { } read)
                return;

            var (container, containerReposne) = read;
            List<Task> cosmosTasks = new();

            foreach (var entity in this.entities)
            {
                var items = await GetItemsForReferenceUpdate(container, entity, finder);

                if (items is not null && items?.Count() > 0)
                {
                    foreach (var item in items)
                    {
                        CosmosDBItemReference propertyItem = map(entity);

                        var id = Convert.ToString(item.GetProperty("id"));
                        PartitionKey partitionKey = Utility.GetPartitionKey(containerReposne, item);

                        var entityId = entity.ID;

                        cosmosTasks.Add(container.PatchItemAsync<DestinationContainer>(id, partitionKey,
                            new[] { PatchOperation.Replace($"/{propertyPath}", propertyItem) })
                            .ContinueWith(x =>
                            {
                                if (!x.IsCompletedSuccessfully)
                                    this.RecordFailure(entityId, containerId, x.Exception);

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

    /// <param name="mapping">
    /// Merges the entity ONTO the stored document and returns what to write. Optional: when omitted, the host's
    /// registered ShiftMapper mapper (<see cref="ShiftMapper.IMapper"/>) copies the entity onto the stored
    /// document through its <c>CreateMap&lt;Entity, CosmosDBItem&gt;()</c>; a member that map ignores — typically
    /// the partition key — survives the merge.
    /// </param>
    public CosmosDbReferenceOperation<DB, Entity> UpdateReference<CosmosDBItem>(string containerId,
        Func<IQueryable<CosmosDBItem>, Entity, IQueryable<CosmosDBItem>> finder,
        Func<Entity, CosmosDBItem, CosmosDBItem>? mapping = null)
    {
        this.cosmosContainerIds.Add(containerId);

        this.upsertActions.Add(async () =>
        {
            //Resolved once, above the loop — see Replicate for why it must never move inside it.
            var merge = mapping ?? ReplicationMapper.ResolveMerge<Entity, CosmosDBItem>(this.services,
                $"UpdateReference<{typeof(CosmosDBItem).Name}>(\"{containerId}\")");

            if (await this.ReadContainerAsync(containerId) is not { } read)
                return;

            var (container, containerResponse) = read;

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
                    //Merge-onto-existing: the stored document is handed in and what comes back is what gets
                    //written, so the delegate (or the mapper's update overload) decides which members survive a
                    //merge. That was never expressible as a plain entity->document map, which is why this
                    //overload exists.
                    mergedDocument = merge(entity, mergedDocument);
                }

                //A successful (or failed) write counts for EVERY source entity that merged into the document.
                var contributors = entry.contributors;

                cosmosTasks.Add(container.UpsertItemAsync<CosmosDBItem>(mergedDocument)
                    .ContinueWith(x =>
                    {
                        foreach (var entity in contributors)
                        {
                            if (!x.IsCompletedSuccessfully)
                                this.RecordFailure(entity.ID, containerId, x.Exception);

                            this.cosmosUpsertSuccesses.AddOrUpdate(entity.ID, SuccessResponse.Create(x.IsCompletedSuccessfully),
                                                                        (key, oldValue) => oldValue.Set(x.IsCompletedSuccessfully));
                        }
                    }));
            }

            await Task.WhenAll(cosmosTasks);
        });

        return this;
    }

    //The container a step writes, and its properties. A container that cannot be read (missing, or not reachable)
    //fails the step's every row: they stay dirty and are reported, and the run goes on with its next step, as the
    //host's next catch-up does after this one. That needs the operation set up with a CosmosClient: one set up with a
    //connection string connects through CosmosClient.CreateAndInitializeAsync, which reads every container the run
    //writes first and throws for a missing one, so the run throws before any step runs.
    private async Task<(Microsoft.Azure.Cosmos.Container Container, ContainerResponse Response)?> ReadContainerAsync(string containerId)
    {
        try
        {
            var container = this.cosmosDatabase.GetContainer(containerId);
            return (container, await container.ReadContainerAsync());
        }
        catch (Exception ex)
        {
            foreach (var entity in this.entities)
            {
                this.RecordFailure(entity.ID, containerId, ex);
                this.cosmosUpsertSuccesses.AddOrUpdate(entity.ID, SuccessResponse.Create(false), (key, oldValue) => oldValue.Set(false));
            }
            return null;
        }
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

    /// <summary>
    /// Replicates the selected rows. A row that fails stays dirty for the next run and does not stop the others, and
    /// failures are logged as a warning. <see cref="RunAndReportAsync"/> returns them as well.
    /// </summary>
    public async Task RunAsync(bool updateAll = false) => await this.RunAndReportAsync(updateAll);

    /// <summary>
    /// Replicates the selected rows and reports the run: the rows it selected, the rows it recorded as replicated, and
    /// every row that failed, with the container and the reason. A row that fails stays dirty for the next run and does
    /// not stop the others. Failures are also logged as a warning; <see cref="CosmosDbReplicationResult.ThrowIfFailed"/>
    /// fails the caller too.
    /// </summary>
    /// <remarks>
    /// A container that cannot be read fails the rows of its step only, and the run goes on with the next step, when
    /// the operation was set up with a <see cref="CosmosClient"/>. Set up with a connection string, the run connects
    /// through <c>CosmosClient.CreateAndInitializeAsync</c>, which reads every container of the run first and throws
    /// for a missing one, so the run throws before any step.
    /// </remarks>
    /// <param name="updateAll">
    /// false selects only the dirty rows. true selects every row, also the rows that look replicated. A row that fails
    /// is marked as not replicated (its <see cref="IShiftEntityReplication.LastReplicationDate"/> is cleared) in both
    /// cases, so the next run retries it.
    /// </param>
    public async Task<CosmosDbReplicationResult> RunAndReportAsync(bool updateAll = false)
    {
        Utility.GuardAgainstShadowedId(typeof(Entity));

        //Return fail replicated entities. A tree document is built from the root loaded the way the save side loads it:
        //with its tree, ignoring query filters and without tracking. Loaded with tracking, in a context an earlier step
        //of the run may have filled, a collection would start with the rows the context already tracks, out of an
        //ordered step's order, and query filters would drop rows the save side's document has.
        var queryable = this.fromTree
            ? this.dbSet.IgnoreQueryFilters().AsNoTracking().IncludeReplicationTree(this.db)
            : this.dbSet.AsQueryable();

        if (!updateAll)
            queryable = await this.WhereDirtyAsync(queryable);

        if (this.query is not null)
            queryable = this.query(queryable);

        var selected = await queryable.ToArrayAsync();
        this.entities = selected;

        //If there is no entities to replicate, terminate the process
        if (selected.Length == 0)
            return new CosmosDbReplicationResult(typeof(Entity).Name, 0, 0, []);

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

        var (replicated, changed) = ApplyReplicationBookkeeping();
        await this.WriteReplicationBookkeepingAsync(changed);

        var result = new CosmosDbReplicationResult(typeof(Entity).Name, selected.Length, replicated,
            this.failures.OrderBy(x => x.EntityId).ThenBy(x => x.ContainerId, StringComparer.Ordinal).ToList());

        this.Dispose();

        if (!result.Succeeded)
            this.services.GetService<ILogger<CosmosDBReplication>>()?.LogWarning(
                "Cosmos DB replication of {Entity}: {Failed} of {Selected} rows failed and stay dirty for the next run. {Failures}",
                result.Entity, result.Failed, result.Selected, result.DescribeFailures());

        return result;
    }

    //Dirty = the replicated-version watermark is behind the row's save date (or absent: never replicated).
    //LastReplicationDate is that watermark — the save date of the replicated version, not a run timestamp.
    private async Task<IQueryable<Entity>> WhereDirtyAsync(IQueryable<Entity> queryable)
    {
        //SQLite keeps DateTimeOffset as text and cannot compare it in SQL, so there the dirty rows are picked in memory
        //from the two watermark columns and then selected by key. The pick ignores query filters; the query that loads
        //the rows still applies them, as it does with the comparison below.
        if (this.db.Database.ProviderName == SqliteProviderName)
        {
            var watermarks = await this.dbSet.IgnoreQueryFilters().AsNoTracking()
                .Select(x => new { x.ID, x.LastSaveDate, x.LastReplicationDate })
                .ToListAsync();

            var dirty = watermarks
                .Where(x => x.LastReplicationDate < x.LastSaveDate || !x.LastReplicationDate.HasValue)
                .Select(x => x.ID)
                .ToList();

            return queryable.Where(x => dirty.Contains(x.ID));
        }

        return queryable.Where(x => x.LastReplicationDate < x.LastSaveDate || !x.LastReplicationDate.HasValue);
    }

    private const string SqliteProviderName = "Microsoft.EntityFrameworkCore.Sqlite";

    //Sets the two replication columns of each selected row to what the run achieved for it, and returns the rows
    //whose columns changed.
    private (int Replicated, List<Entity> Changed) ApplyReplicationBookkeeping()
    {
        //Success implies a pending stamp exists: the upsert action records it before queueing the Cosmos call,
        //and rows whose mapping or stamp computation threw are marked unsuccessful. The TryGetValue keeps the
        //unreachable missing-stamp case on the safe side — the row stays dirty and is retried, rather than being
        //marked clean with stale coordinates.
        //
        //A row with a failure is marked as not replicated. A forced run (updateAll) also selects rows that look
        //replicated, and such a row used to keep its clean watermark when it failed. The next run, which selects only
        //dirty rows, then never retried it, although the report said that it stays dirty.
        var failed = this.failures.Select(x => x.EntityId).ToHashSet();
        var replicated = 0;
        var changed = new List<Entity>();

        foreach (var entity in this.entities)
        {
            var before = (entity.LastReplicationDate, entity.LastReplicationStamp);

            if (failed.Contains(entity.ID))
            {
                entity.MarkReplicationFailed();
            }
            else if (this.cosmosUpsertSuccesses.GetOrAdd(entity.ID, new SuccessResponse()).Get() &&
                this.pendingStamps.TryGetValue(entity.ID, out var stamp))
            {
                entity.MarkReplicated(stamp);
                replicated++;
            }

            if (before != (entity.LastReplicationDate, entity.LastReplicationStamp))
                changed.Add(entity);
        }

        return (replicated, changed);
    }

    //Writes the two replication columns of the rows ApplyReplicationBookkeeping changed, by key and set-based, many
    //rows a statement, in one transaction, and nothing else. It is not a save, so host triggers, the audit
    //backfill, replication and the marking of replication trees do not run for it, as they did not run for the save
    //without triggers that this replaced. Rows loaded with tracking stay loaded in this scope's context with the new
    //values accepted, as that save left them.
    private async Task WriteReplicationBookkeepingAsync(List<Entity> changed)
    {
        await this.db.SaveReplicationBookkeepingAsync(changed);

        //Tree roots are loaded without tracking.
        if (this.fromTree)
            return;

        var type = this.db.Model.FindEntityType(typeof(Entity))!;
        var dateProperty = type.FindProperty(nameof(IShiftEntityReplication.LastReplicationDate))!;
        var stampProperty = type.FindProperty(nameof(IShiftEntityReplication.LastReplicationStamp))!;
        var tracker = this.db.ChangeTracker;
        var autoDetect = tracker.AutoDetectChangesEnabled;
        tracker.AutoDetectChangesEnabled = false;
        try
        {
            foreach (var entity in changed)
            {
                var entry = this.db.Entry(entity);
                if (entry.State == EntityState.Detached)
                    continue;

                var date = entry.Property(dateProperty);
                var stamp = entry.Property(stampProperty);

                date.OriginalValue = date.CurrentValue;
                date.IsModified = false;
                stamp.OriginalValue = stamp.CurrentValue;
                stamp.IsModified = false;
            }
        }
        finally
        {
            tracker.AutoDetectChangesEnabled = autoDetect;
        }
    }

    private void RecordFailure(long entityId, string containerId, Exception? exception) =>
        this.failures.Enqueue(new CosmosDbReplicationFailure(entityId, containerId, Utility.DescribeFailure(exception)));

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
        this.failures = null!;

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
