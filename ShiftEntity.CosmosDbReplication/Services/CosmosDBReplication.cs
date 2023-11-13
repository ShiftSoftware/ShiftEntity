﻿using ShiftSoftware.ShiftEntity.EFCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ShiftSoftware.ShiftEntity.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using AutoMapper;
using Microsoft.Azure.Cosmos;
using EntityFrameworkCore.Triggered.Extensions;
using ShiftSoftware.ShiftEntity.EFCore.Entities;
using System.Runtime.CompilerServices;

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
public class CosmosDbReplicationOperations<DB, Entity>
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

    private string containerId;

    private IEnumerable<Entity> entities;
    private IEnumerable<DeletedRowLog> deletedRows;

    List<Func<Task>> upsertActions = new();
    List<Func<Task>> deleteActions = new();
    Dictionary<long, SuccessResponse> cosmosDeleteSuccesses = new();
    Dictionary<long, SuccessResponse> cosmosUpsertSuccesses = new();
    Dictionary<long, bool> cosmosDeleteFails = new ();

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
        this.containerId = containerId;

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

            List < Task> cosmosTasks = new ();

            foreach (var entity in this.entities)
            {
                CosmosDBItem item;
                if (mapping is not null)
                    item = mapping(entity);
                else
                    item = this.mapper.Map<CosmosDBItem>(entity);

                cosmosTasks.Add(container.UpsertItemAsync<CosmosDBItem>(item)
                    .ContinueWith(x =>
                    {
                        if (!this.cosmosUpsertSuccesses.ContainsKey(entity.ID))
                            this.cosmosUpsertSuccesses[entity.ID] = new SuccessResponse { Current = x.IsCompletedSuccessfully };
                        else
                            this.cosmosUpsertSuccesses[entity.ID].Current =
                                this.cosmosUpsertSuccesses[entity.ID].Current && x.IsCompletedSuccessfully;
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

            foreach (var deletedRow in this.deletedRows)
            {
                var key = GetPartitionKey(deletedRow);

                cosmosTasks.Add(container.DeleteItemAsync<CosmosDBItem>(deletedRow.RowID.ToString(), key)
                    .ContinueWith(x =>
                    {
                        if (!this.cosmosDeleteSuccesses.ContainsKey(deletedRow.ID))
                            this.cosmosDeleteSuccesses[deletedRow.ID] = new SuccessResponse { Current = x.IsCompletedSuccessfully };
                        else
                            this.cosmosDeleteSuccesses[deletedRow.ID].Current =
                                this.cosmosDeleteSuccesses[deletedRow.ID].Current && x.IsCompletedSuccessfully;
                    }));
            }

            await Task.WhenAll(cosmosTasks);
        });

        return this;
    }

    private PartitionKey GetPartitionKey(DeletedRowLog row)
    {
        var builder = new PartitionKeyBuilder();

        AddPrtitionKey(builder, row.PartitionKeyLevelOneValue, row.PartitionKeyLevelOneType);
        AddPrtitionKey(builder, row.PartitionKeyLevelTwoValue, row.PartitionKeyLevelTwoType);
        AddPrtitionKey(builder, row.PartitionKeyLevelThreeValue, row.PartitionKeyLevelThreeType);


        return builder.Build();
    }

    private void AddPrtitionKey(PartitionKeyBuilder builder, string? value, PartitionKeyTypes type)
    {
        if (type == PartitionKeyTypes.String)
            builder.Add(value);
        else if (type == PartitionKeyTypes.Numeric)
            builder.Add(Double.Parse(value));
        else if(type== PartitionKeyTypes.Boolean)
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
        this.deletedRows = await db.DeletedRowLogs.Where(x => x.ContainerName == this.containerId)
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
    }

    private void UpdateLastReplicationDatesAsync()
    {
        foreach (var entity in this.entities)
            if (this.cosmosUpsertSuccesses.GetValueOrDefault(entity.ID, new SuccessResponse()).Current)
                entity.UpdateReplicationDate();
    }

    private void DeleteReplicatedDeletedRowLogs()
    {
        foreach (var row in this.deletedRows)
            if (this.cosmosDeleteSuccesses.GetValueOrDefault(row.ID, new SuccessResponse()).Current)
                db.DeletedRowLogs.Remove(row);
    }

    private void ResetUpsertSuccess()
    {
        this.cosmosUpsertSuccesses = this.cosmosUpsertSuccesses
            .ToDictionary(x => x.Key, x => new SuccessResponse { Previous = x.Value?.Current, Current = false });
    }

    private void ResetDeleteSuccess()
    {
        this.cosmosDeleteSuccesses = this.cosmosDeleteSuccesses
            .ToDictionary(x => x.Key, x => new SuccessResponse { Previous = x.Value?.Current, Current = false });
    }
}

class SuccessResponse
{
    public bool? Previous { get; set; }
    public bool Current { get; set; }
}
