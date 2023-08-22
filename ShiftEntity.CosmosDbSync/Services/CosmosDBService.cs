using AutoMapper;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.Core;
using System;
using System.Collections.Generic;
using System.Dynamic;
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
        var dto = mapper.Map(entity, typeof(EntityType), cosmosDbItemType);

        //Upsert to cosmosdb
        using CosmosClient client = new(cosmosDbConnectionString);
        var db = client.GetDatabase(cosmosDbDatabaseName);
        var container = db.GetContainer(containerName);

        dynamic item = new ExpandoObject();
        item.id = entity.ID.ToString();
        CopyProperties(dto, item);

        var response = await container.UpsertItemAsync(item);

        if (response.StatusCode == System.Net.HttpStatusCode.OK ||
            response.StatusCode == System.Net.HttpStatusCode.Created)
        {
            await UpdateLastSyncDateAsync(entity);
        }
    }

    private void CopyProperties(object source, dynamic destination)
    {
        PropertyInfo[] properties = source.GetType().GetProperties();

        foreach (PropertyInfo property in properties)
        {
            object value = property.GetValue(source);
            ((IDictionary<string, object>)destination)[property.Name] = value;
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
