using AutoMapper;
using Microsoft.Azure.Cosmos;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.CosmosDbReplication.Exceptions;
using ShiftSoftware.ShiftEntity.CosmosDbReplication.Extensions;
using ShiftSoftware.ShiftEntity.Model.Replication;
using System.Dynamic;
using System.Reflection;

namespace ShiftSoftware.ShiftEntity.CosmosDbReplication.Services;

internal class CosmosDBService<EntityType>
    where EntityType : ShiftEntity<EntityType>
{
    private readonly IMapper mapper;
    private readonly IEnumerable<DbContextProvider> dbContextProviders;

    public CosmosDBService(IMapper mapper, IEnumerable<DbContextProvider> dbContextProviders)
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
        var partitionKey = GetPartitionKey(dto);

        ItemResponse<ExpandoObject> response;

        if (partitionKey.partitionKey is null)
            response = await container.UpsertItemAsync(item);
        else
            response = await container.UpsertItemAsync(item, partitionKey.partitionKey.Value);

        if (response.StatusCode == System.Net.HttpStatusCode.OK ||
            response.StatusCode == System.Net.HttpStatusCode.Created ||
            response.StatusCode == System.Net.HttpStatusCode.NoContent)
        {
            await UpdateLastReplicationDateAsync(entity);
        }
    }

    private (PartitionKey? partitionKey, string? value, PartitionKeyTypes type) GetPartitionKey(object item)
    {
        string? partitionKeyName = null;

        var attribute = (ReplicationPartitionKeyAttribute)item.GetType().GetCustomAttributes(true).LastOrDefault(x => x as ReplicationPartitionKeyAttribute != null)!;

        if (attribute is null || attribute?.PropertyName is null)
            return (null, null, PartitionKeyTypes.None);

        partitionKeyName = attribute.PropertyName;

        var property = item.GetType().GetProperty(partitionKeyName);
        if (property is null)
            throw new WrongPartitionKeyNameException($"Can not find {partitionKeyName} in the object for partition key");

        Type propertyType = property.PropertyType;
        if (!(propertyType == typeof(bool?) || propertyType == typeof(bool) || propertyType == typeof(string) || propertyType.IsNumericType()))
            throw new WrongPartitionKeyTypeException("Only boolean or number or string partition key types allowed");

        var value = property.GetValue(item);

        if (propertyType == typeof(bool?) || propertyType == typeof(bool))
            return (new PartitionKey(Convert.ToBoolean(value)), Convert.ToString(value), PartitionKeyTypes.Boolean);
        else if (propertyType == typeof(string))
            return (new PartitionKey(Convert.ToString(value)), Convert.ToString(value), PartitionKeyTypes.String);
        else
            return (new PartitionKey(Convert.ToDouble(value)), Convert.ToString(value), PartitionKeyTypes.Numeric);
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

    private async Task UpdateLastReplicationDateAsync(EntityType entity)
    {
        foreach (var provider in dbContextProviders)
        {
            using var dbContext = provider.ProvideDbContext();

            if (dbContext.Model.GetEntityTypes().Any(x => x.ClrType == typeof(EntityType)))
            {
                entity.UpdateReplicationDate();
                dbContext.Attach(entity);
                dbContext.Entry(entity).Property(x => x.LastReplicationDate).IsModified = true;
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

        ItemResponse<dynamic> response;

        if (partitionKey.partitionKey is null)
            response = await container.DeleteItemAsync<dynamic>(entity.ID.ToString(), PartitionKey.None);
        else
            response = await container.DeleteItemAsync<dynamic>(entity.ID.ToString(), partitionKey.partitionKey.Value);

        if (response.StatusCode == System.Net.HttpStatusCode.OK ||
            response.StatusCode == System.Net.HttpStatusCode.Created ||
            response.StatusCode == System.Net.HttpStatusCode.NoContent)
        {
            await UpdateDeleteRowLogLastReplicationDateAsync(entity, partitionKey.value, partitionKey.type);
        }
    }

    private async Task UpdateDeleteRowLogLastReplicationDateAsync(EntityType entity,
        string? partitionKeyValue,
        PartitionKeyTypes partitionKeyType)
    {
        var entityName = entity.GetType().Name;

        foreach (var provider in dbContextProviders)
        {
            using var dbContext = provider.ProvideDbContext();

            if (dbContext.Model.GetEntityTypes().Any(x => x.ClrType == typeof(EntityType)))
            {
                var log = dbContext.DeletedRowLogs.SingleOrDefault(x =>
                    x.RowID == entity.ID &&
                    x.PartitionKeyValue == partitionKeyValue &&
                    x.PartitionKeyType == partitionKeyType &&
                    x.EntityName == entityName
                );

                if (log is not null)
                {
                    log.LastReplicationDate = DateTime.UtcNow;
                    await dbContext.SaveChangesAsync();
                }
            }
        }
    }
}


