using AutoMapper;
using Microsoft.Azure.Cosmos;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.CosmosDbSync.Exceptions;
using ShiftSoftware.ShiftEntity.CosmosDbSync.Extensions;
using System.Dynamic;
using System.Reflection;

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
        var partitionKey = GetPartitionKey(item);

        ItemResponse<ExpandoObject> response;

        if (partitionKey is null)
            response = await container.UpsertItemAsync(item);
        else
            response = await container.UpsertItemAsync(item, partitionKey);
            
        if (response.StatusCode == System.Net.HttpStatusCode.OK ||
            response.StatusCode == System.Net.HttpStatusCode.Created)
        {
            await UpdateLastSyncDateAsync(entity);
        }
    }

    private PartitionKey? GetPartitionKey(object item)
    {
        string? partitionKeyName = null;
        
        var attribute = (SyncPartitionKeyAttribute)item.GetType().GetCustomAttributes(true).LastOrDefault(x => x as SyncPartitionKeyAttribute != null)!;

        if (attribute is null || attribute?.PropertyName is null)
            return null;

        partitionKeyName = attribute.PropertyName;

        var property = item.GetType().GetProperty(partitionKeyName);
        if (property is null)
            throw new WrongPartitionKeyNameException($"Can not find {partitionKeyName} in the object");

        Type propertyType = property.PropertyType;
        if (!(propertyType == typeof(bool?) || propertyType == typeof(bool) || propertyType == typeof(string) || propertyType.IsNumericType()))
            throw new WrongPartitionKeyTypeException("Only boolean or number or string partition key types allowed");

        var value = property.GetValue(item);

        if (propertyType == typeof(bool?) || propertyType == typeof(bool))
            return new PartitionKey(Convert.ToBoolean(value));
        else if (propertyType == typeof(string))
            return new PartitionKey(Convert.ToString(value));
        else
            return new PartitionKey(Convert.ToDouble(value));
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

        var dto = mapper.Map(entity, typeof(EntityType), cosmosDbItemType);

        var partitionKey = GetPartitionKey(dto);

        if (partitionKey is null)
            await container.DeleteItemAsync<dynamic>(entity.ID.ToString(), PartitionKey.None);
        else
            await container.DeleteItemAsync<dynamic>(entity.ID.ToString(), partitionKey.Value);
    }
}


