using AutoMapper;
using EntityFrameworkCore.Triggered.Extensions;
using Microsoft.Azure.Cosmos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.CosmosDbReplication.Exceptions;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.EFCore.Entities;
using System.ComponentModel;
using System.Drawing.Imaging;
using System.Dynamic;
using System.Linq;
using System.Linq.Expressions;
using System.Net;
using System.Runtime.CompilerServices;
using System.Transactions;

namespace ShiftSoftware.ShiftEntity.CosmosDbReplication.Services;

public class CosmosDBReplication
{
    private readonly IServiceProvider services;

    public CosmosDBReplication(IServiceProvider services)
    {
        this.services = services;
    }

    public CosmosDbReplicationOperations<DB, Entity> SetUp<DB, Entity>(string cosmosDbConnectionString, string cosmosDataBaseId,
        Func<IQueryable<Entity>, IQueryable<Entity>>? query = null)
        where DB : ShiftDbContext
        where Entity : ShiftEntity<Entity>
    {
        return new CosmosDbReplicationOperations<DB, Entity>(cosmosDbConnectionString, cosmosDataBaseId, services, query);
    }
}
public class CosmosDbReplicationOperations<DB, Entity> : IDisposable
    where DB : ShiftDbContext
    where Entity : ShiftEntity<Entity>
{
    private readonly string cosmosDbConnectionString;
    private readonly string cosmosDbDatabaseId;
    private readonly IServiceProvider services;
    private readonly IMapper mapper;
    private readonly DB db;
    private readonly DbSet<Entity> dbSet;
    private readonly Func<IQueryable<Entity>, IQueryable<Entity>>? query;

    private IEnumerable<Entity> entities;
    private IEnumerable<DeletedRowLog> deletedRows;

    private List<string> containerIds = new();

    List<Func<Task>> upsertActions = new();
    List<Func<Task>> deleteActions = new();
    Dictionary<long, SuccessResponse> cosmosDeleteSuccesses = new();
    Dictionary<long, SuccessResponse> cosmosUpsertSuccesses = new();

    public CosmosDbReplicationOperations(
        string cosmosDbConnectionString,
        string cosmosDbDatabaseId,
        IServiceProvider services,
        Func<IQueryable<Entity>, IQueryable<Entity>>? query)
    {
        this.cosmosDbConnectionString = cosmosDbConnectionString;
        this.cosmosDbDatabaseId = cosmosDbDatabaseId;
        this.services = services;
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
    public CosmosDbReplicationOperations<DB, Entity> Replicate<CosmosDBItem>(string containerId, Func<Entity, CosmosDBItem>? mapping = null)
    {
        this.containerIds.Add(containerId);

        //Upsert fail replicated entities into cosmos container
        this.upsertActions.Add(async () =>
        {
            using var client = await CosmosClient.CreateAndInitializeAsync(cosmosDbConnectionString,
            new List<(string, string)> { (cosmosDbDatabaseId, containerId) }, new CosmosClientOptions()
            {
                AllowBulkExecution = true
            });
            var db = client.GetDatabase(cosmosDbDatabaseId);
            var container = db.GetContainer(containerId);

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
                        if (!this.cosmosUpsertSuccesses.ContainsKey(entityId))
                            this.cosmosUpsertSuccesses[entityId] = SuccessResponse.Create(x.IsCompletedSuccessfully);
                        else
                            this.cosmosUpsertSuccesses[entityId].Set(x.IsCompletedSuccessfully);
                    }));
            }

            await Task.WhenAll(cosmosTasks);
        });

        //Delete fail deleted rows from cosmos container
        this.deleteActions.Add(async () =>
        {
            using var client = await CosmosClient.CreateAndInitializeAsync(cosmosDbConnectionString,
            new List<(string, string)> { (cosmosDbDatabaseId, containerId) }, new CosmosClientOptions()
            {
                AllowBulkExecution = true
            });
            var db = client.GetDatabase(cosmosDbDatabaseId);
            var container = db.GetContainer(containerId);

            List<Task> cosmosTasks = new();

            foreach (var deletedRow in this.deletedRows.Where(x => x.ContainerName == containerId))
            {
                var key = GetPartitionKey(deletedRow);

                cosmosTasks.Add(container.DeleteItemAsync<CosmosDBItem>(deletedRow.RowID.ToString(), key)
                    .ContinueWith(x =>
                    {
                        CosmosException ex = null;

                        if (x.Exception != null)
                            foreach (var innerException in x.Exception.InnerExceptions)
                                if (innerException is CosmosException customException)
                                    ex = customException;

                        bool success = x.IsCompletedSuccessfully || ex?.StatusCode == HttpStatusCode.NotFound;

                        if (!this.cosmosDeleteSuccesses.ContainsKey(deletedRow.ID))
                            this.cosmosDeleteSuccesses[deletedRow.ID] = SuccessResponse.Create(success);
                        else
                            this.cosmosDeleteSuccesses[deletedRow.ID].Set(success);
                    }));
            }

            await Task.WhenAll(cosmosTasks);
        });

        return this;
    }

    public CosmosDbReplicationOperations<DB, Entity> UpdatePropertyReference<CosmosDBItemReference, DestinationContainer>(
        string destinationContainerId, Expression<Func<DestinationContainer, object>> destinationReferencePropertyExpression,
        Func<Entity, CosmosDBItemReference>? mapping = null)
    {
        if (destinationReferencePropertyExpression.Body is MemberExpression memberExpression)
        {
            string propertyName = memberExpression.Member.Name;
            return UpdatePropertyReference<CosmosDBItemReference>(destinationContainerId, propertyName, mapping);
        }
        else
        {
            throw new ArgumentException("Expression must be a member access expression");
        }
    }

    public CosmosDbReplicationOperations<DB, Entity> UpdatePropertyReference<CosmosDBItemReference>(
        string destinationContainerId, string destinationReferencePropertyName,
        Func<Entity, CosmosDBItemReference>? mapping = null)
    {
        //Update reference
        this.upsertActions.Add(async () =>
        {
            using var client = await CosmosClient.CreateAndInitializeAsync(this.cosmosDbConnectionString,
            new List<(string, string)> { (this.cosmosDbDatabaseId, destinationContainerId) }, new CosmosClientOptions()
            {
                AllowBulkExecution = true
            });
            var db = client.GetDatabase(this.cosmosDbDatabaseId);
            var container = db.GetContainer(destinationContainerId);

            var containerReposne = await container.ReadContainerAsync();

            var items = await GetItemsForPropertyReferenceUpdateAsync(container, destinationReferencePropertyName,
                this.entities.Select(x => x.ID.ToString()));

            var referencedIds = items.Select(i => Convert.ToString(i[destinationReferencePropertyName].id));
            var successIdsWithoutReference = this.cosmosUpsertSuccesses
                .Where(x => !((bool)referencedIds.Contains(x.Key.ToString())));

            //Reset successes that not have reference to its previous status 
            foreach (var success in successIdsWithoutReference)
                success.Value.ResetToPreviousState();

            List<Task> cosmosTasks = new();

            foreach (var item in items)
            {
                var entity = this.entities.FirstOrDefault(x => x.ID.ToString() == Convert.ToString(item[destinationReferencePropertyName].id))!;

                CosmosDBItemReference propertyItem;
                if (mapping is not null)
                    propertyItem = mapping(entity);
                else
                    propertyItem = this.mapper.Map<CosmosDBItemReference>(entity);

                var id = Convert.ToString(item.id);
                PartitionKey partitionKey = GetPartitionKey(containerReposne, item);

                var entityId = entity.ID;

                cosmosTasks.Add(((Task<ItemResponse<dynamic>>)container.PatchItemAsync<dynamic>(id, partitionKey,
                    new[] { PatchOperation.Replace($"/{destinationReferencePropertyName}", propertyItem) },
                    new PatchItemRequestOptions { IfMatchEtag = item._etag }))
                    .ContinueWith(x =>
                    {
                        if (!this.cosmosUpsertSuccesses.ContainsKey(entityId))
                            this.cosmosUpsertSuccesses[entityId] = SuccessResponse.Create(x.IsCompletedSuccessfully);
                        else
                            this.cosmosUpsertSuccesses[entityId].Set(x.IsCompletedSuccessfully);
                    }));
            }

            await Task.WhenAll(cosmosTasks);
        });

        //Delete references
        this.deleteActions.Add(async () =>
        {
            using var client = await CosmosClient.CreateAndInitializeAsync(this.cosmosDbConnectionString,
            new List<(string, string)> { (this.cosmosDbDatabaseId, destinationContainerId) }, new CosmosClientOptions()
            {
                AllowBulkExecution = true
            });
            var db = client.GetDatabase(this.cosmosDbDatabaseId);
            var container = db.GetContainer(destinationContainerId);

            var containerReposne = await container.ReadContainerAsync();

            var items = await GetItemsForPropertyReferenceUpdateAsync(container, destinationReferencePropertyName,
                this.deletedRows.Select(x => x.RowID.ToString()));

            var referencedIds = items.Select(i => Convert.ToString(i[destinationReferencePropertyName].id));
            var successIdsWithoutReference = this.cosmosDeleteSuccesses
                .Where(x => !((bool)referencedIds.Contains(x.Key.ToString())));

            //Reset successes that not have reference to its previous status 
            foreach (var success in successIdsWithoutReference)
                success.Value.ResetToPreviousState();

            List<Task> cosmosTasks = new();

            foreach (var item in items)
            {
                var id = Convert.ToString(item.id);
                PartitionKey partitionKey = GetPartitionKey(containerReposne, item);

                var rowId = ((string)Convert.ToString(item[destinationReferencePropertyName].id)).ToLong();

                cosmosTasks.Add(((Task<ItemResponse<dynamic>>)container.PatchItemAsync<dynamic>(id, partitionKey,
                    new[] { PatchOperation.Remove($"/{destinationReferencePropertyName}") },
                    new PatchItemRequestOptions { IfMatchEtag = item._etag }))
                    .ContinueWith(x =>
                    {
                        if (!this.cosmosDeleteSuccesses.ContainsKey(rowId))
                            this.cosmosDeleteSuccesses[rowId] = SuccessResponse.Create(x.IsCompletedSuccessfully);
                        else
                            this.cosmosDeleteSuccesses[rowId].Set(x.IsCompletedSuccessfully);
                    }));
            }

            await Task.WhenAll(cosmosTasks);
        });

        return this;
    }

    public async Task<IEnumerable<dynamic>> GetItemsForPropertyReferenceUpdateAsync(Microsoft.Azure.Cosmos.Container container, 
        string referencePropertyName,
        IEnumerable<string> ids)
    {
        var query = $"SELECT * FROM c WHERE ARRAY_CONTAINS(@ids, c.{referencePropertyName}.id)";

        var parameterizedQuery = new QueryDefinition(
          query: query
        ).WithParameter("@ids", ids);

        var filteredFeed = container.GetItemQueryIterator<dynamic>(
            queryDefinition: parameterizedQuery
        );

        List<dynamic> items = new();

        while (filteredFeed.HasMoreResults)
        {
            items.AddRange(await filteredFeed.ReadNextAsync());
        }

        return items;
    }

    private PartitionKey GetPartitionKey(DeletedRowLog row)
    {
        var builder = new PartitionKeyBuilder();

        AddPrtitionKey(builder, row.PartitionKeyLevelOneValue, row.PartitionKeyLevelOneType);
        AddPrtitionKey(builder, row.PartitionKeyLevelTwoValue, row.PartitionKeyLevelTwoType);
        AddPrtitionKey(builder, row.PartitionKeyLevelThreeValue, row.PartitionKeyLevelThreeType);


        return builder.Build();
    }

    private PartitionKey GetPartitionKey(ContainerResponse containerResponse, dynamic item)
    {
        PartitionKeyBuilder partitionKeyBuilder = new PartitionKeyBuilder();

        foreach (var partitionKeyPath in containerResponse.Resource.PartitionKeyPaths)
        {
            var value = (JValue)item[partitionKeyPath.Substring(1)];

            if (value.Value.GetType() == typeof(string))
                partitionKeyBuilder.Add(value.Value<string>());
            else if (value.Value.GetType().IsNumericType())
                partitionKeyBuilder.Add(value.Value<double>());
            else if (value.Value.GetType() == typeof(bool) || value.Value.GetType() == typeof(bool?))
                partitionKeyBuilder.Add(value.Value<bool>());
            else
                throw new ArgumentException($"The type or value of '{partitionKeyPath}' partition key is incorrect");
        }

        return partitionKeyBuilder.Build();
    }

    private void AddPrtitionKey(PartitionKeyBuilder builder, string? value, PartitionKeyTypes type)
    {
        if (type == PartitionKeyTypes.String)
            builder.Add(value);
        else if (type == PartitionKeyTypes.Numeric)
            builder.Add(Double.Parse(value));
        else if (type == PartitionKeyTypes.Boolean)
            builder.Add(Boolean.Parse(value));
    }

    public async Task RunAsync()
    {
        //Return fail replicated entities
        var queryable = this.dbSet.Where(x => x.LastReplicationDate < x.LastSaveDate ||
            !x.LastReplicationDate.HasValue);

        if (this.query is not null)
            queryable = this.query(queryable);

        this.entities = await queryable.ToArrayAsync();

        //Return delete rows that failed to replicate
        this.deletedRows = await db.DeletedRowLogs.Where(x => this.containerIds.Contains(x.ContainerName))
            .ToArrayAsync();

        foreach (var action in this.upsertActions)
        {
            this.ResetUpsertSuccess();
            await action.Invoke();
        }

        foreach (var action in this.deleteActions)
        {
            this.ResetDeleteSuccess();
            await action.Invoke();
        }

        UpdateLastReplicationDatesAsync();
        DeleteReplicatedDeletedRowLogs();

        await this.db.SaveChangesWithoutTriggersAsync();

        this.Dispose();
    }

    private void UpdateLastReplicationDatesAsync()
    {
        foreach (var entity in this.entities)
            if (this.cosmosUpsertSuccesses.GetValueOrDefault(entity.ID, new SuccessResponse()).Get())
                entity.UpdateReplicationDate();
    }

    private void DeleteReplicatedDeletedRowLogs()
    {
        foreach (var row in this.deletedRows)
            if (this.cosmosDeleteSuccesses.GetValueOrDefault(row.ID, new SuccessResponse()).Get())
                db.DeletedRowLogs.Remove(row);
    }

    private void ResetUpsertSuccess()
    {
        this.cosmosUpsertSuccesses = this.cosmosUpsertSuccesses
            .ToDictionary(x => x.Key, x => x.Value?.Reset() ?? new SuccessResponse());
    }

    private void ResetDeleteSuccess()
    {
        this.cosmosDeleteSuccesses = this.cosmosDeleteSuccesses
            .ToDictionary(x => x.Key, x => x.Value?.Reset() ?? new SuccessResponse());
    }

    public void Dispose()
    {
        this.entities = null!;
        this.deletedRows = null!;

        this.containerIds = null!;

        this.upsertActions = null!;
        this.deleteActions = null!;
        this.cosmosDeleteSuccesses = null!;
        this.cosmosUpsertSuccesses = null!;
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
