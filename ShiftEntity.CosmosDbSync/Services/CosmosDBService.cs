using AutoMapper;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using static Microsoft.EntityFrameworkCore.DbLoggerCategory;

namespace ShiftSoftware.ShiftEntity.CosmosDbSync.Services;

internal class CosmosDBService<EntityType>
    where EntityType : ShiftEntity<EntityType>
{
    private readonly IMapper mapper;
    private readonly IEnumerable<IDbContextProvider> dbContextProviders;

    public CosmosDBService(IMapper mapper,IEnumerable<IDbContextProvider> dbContextProviders)
    {
        this.mapper = mapper;
        this.dbContextProviders = dbContextProviders;
    }

    internal async Task UpsertAsync(EntityType entity,
        Type cosmosDbItemType,
        string containerName,
        string cosmosDbDatabaseName,
        string cosmosDbConnectionString)
    {
        var item = mapper.Map(entity, typeof(EntityType), cosmosDbItemType);

        //Upsert to cosmosdb
        using CosmosClient client = new(cosmosDbConnectionString);
        var db = client.GetDatabase(cosmosDbDatabaseName);
        var container = db.GetContainer(containerName);

        try
        {
            var response = await container.UpsertItemAsync(new { id = entity.ID.ToString(), item = item });

            if (response.StatusCode == System.Net.HttpStatusCode.OK ||
                response.StatusCode == System.Net.HttpStatusCode.Created)
            {
                await UpdateLastSyncDateAsync(entity);
            }
        }
        catch (Exception ex)
        {
        }
    }

    private async Task UpdateLastSyncDateAsync(EntityType entity)
    {
        foreach (var provider in dbContextProviders)
        {
            using var dbContext = provider.ProvideDbContext();

            if (dbContext.Model.GetEntityTypes().Any(x => x.ClrType == typeof(EntityType)))
            {
                entity.UpdateSyncDate();
                dbContext.Entry(entity).State = Microsoft.EntityFrameworkCore.EntityState.Modified;
                await dbContext.SaveChangesAsync();
            }
        }
    }

    internal async Task DeleteAsync(EntityType entity,
        Type cosmosDbItemType,
        string containerName,
        string cosmosDbDatabaseName,
        string cosmosDbConnectionString)
    {

        //Delete from cosmosdb
        using CosmosClient client = new(cosmosDbConnectionString);
        var db = client.GetDatabase(cosmosDbDatabaseName);
        var container = db.GetContainer(containerName);

        await container.DeleteItemAsync<dynamic>(entity.ID.ToString(), PartitionKey.None);
    }
}
