using AutoMapper;
using EntityFrameworkCore.Triggered.Extensions;
using Microsoft.Azure.Cosmos;
using Microsoft.EntityFrameworkCore;
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
        var partitionKey = GetPartitionKey(entity, dto);

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

    private (PartitionKey? partitionKey,
        (string? value, PartitionKeyTypes type)? level1,
        (string? value, PartitionKeyTypes type)? level2,
        (string? value, PartitionKeyTypes type)? level3) GetPartitionKey(EntityType item, object dto)
    {
        var builder = new PartitionKeyBuilder();

        var attribute = (ReplicationPartitionKeyAttribute)item.GetType().GetCustomAttributes(true).LastOrDefault(x => x as ReplicationPartitionKeyAttribute != null)!;

        if (attribute is null || attribute?.KeyLevelOnePropertyName is null)
            return (null, null, null, null);

        var keyLevel1 = attribute.KeyLevelOnePropertyName;
        var keyLevel2 = attribute.KeyLevelTwoPropertyName;
        var keyLevel3 = attribute.KeyLevelThreePropertyName;

        var propertyLevel1 = dto.GetType().GetProperty(keyLevel1);
        if (propertyLevel1 is null)
            throw new WrongPartitionKeyNameException($"Can not find {keyLevel1} in the object for partition key");

        PropertyInfo? propertyLevel2;
        if (keyLevel2 is not null)
        {
            propertyLevel2 = dto.GetType().GetProperty(keyLevel2);
            if (propertyLevel2 is null)
                throw new WrongPartitionKeyNameException($"Can not find {keyLevel2} in the object for partition key");
        }
        else
        {
            propertyLevel2 = null;
        }

        PropertyInfo? propertyLevel3;
        if (keyLevel3 is not null)
        {
            propertyLevel3 = dto.GetType().GetProperty(keyLevel3);
            if (propertyLevel3 is null)
                throw new WrongPartitionKeyNameException($"Can not find {keyLevel3} in the object for partition key");
        }
        else
        {
            propertyLevel3 = null;
        }

        var level1 = GetPartitionKey(propertyLevel1, dto, builder);

        (string? value, PartitionKeyTypes type)? level2;
        if (propertyLevel2 is not null)
            level2 = GetPartitionKey(propertyLevel2!, dto, builder);
        else
            level2 = null;

        (string? value, PartitionKeyTypes type)? level3;
        if (propertyLevel3 is not null)
            level3 = GetPartitionKey(propertyLevel3!, dto, builder);
        else
            level3 = null;

        return (builder.Build(), level1, level2, level3);
    }

    private (string? value, PartitionKeyTypes type) GetPartitionKey(PropertyInfo property,
        object dto,
        PartitionKeyBuilder builder)
    {
        Type propertyType = property.PropertyType;
        if (!(propertyType == typeof(bool?) || propertyType == typeof(bool) || propertyType == typeof(string) || propertyType.IsNumericType()))
            throw new WrongPartitionKeyTypeException($"Only boolean or number or string partition key types allowed for propery {property.Name}");

        var value = property.GetValue(dto);

        if (propertyType == typeof(bool?) || propertyType == typeof(bool))
        {
            builder.Add(Convert.ToBoolean(value));
            return (Convert.ToString(value), PartitionKeyTypes.Boolean);
        }
        else if (propertyType == typeof(string))
        {
            builder.Add(Convert.ToString(value));
            return (Convert.ToString(value), PartitionKeyTypes.String);
        }
        else
        {
            builder.Add(Convert.ToDouble(value));
            return (Convert.ToString(value), PartitionKeyTypes.Numeric);
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
                await dbContext.SaveChangesWithoutTriggersAsync();
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

        var partitionKey = GetPartitionKey(entity, dto);

        ItemResponse<dynamic> response;

        if (partitionKey.partitionKey is null)
            response = await container.DeleteItemAsync<dynamic>(entity.ID.ToString(), PartitionKey.None);
        else
            response = await container.DeleteItemAsync<dynamic>(entity.ID.ToString(), partitionKey.partitionKey.Value);

        if (response.StatusCode == System.Net.HttpStatusCode.OK ||
            response.StatusCode == System.Net.HttpStatusCode.Created ||
            response.StatusCode == System.Net.HttpStatusCode.NoContent)
        {
            await UpdateDeleteRowLogLastReplicationDateAsync(entity, partitionKey.level1,
                partitionKey.level2, partitionKey.level3);
        }
    }

    private async Task UpdateDeleteRowLogLastReplicationDateAsync(EntityType entity,
        (string? value, PartitionKeyTypes type)? level1,
        (string? value, PartitionKeyTypes type)? level2,
        (string? value, PartitionKeyTypes type)? level3)
    {
        var entityName = entity.GetType().Name;

        foreach (var provider in dbContextProviders)
        {
            using var dbContext = provider.ProvideDbContext();

            if (dbContext.Model.GetEntityTypes().Any(x => x.ClrType == typeof(EntityType)))
            {
                var query = dbContext.DeletedRowLogs.Where(x =>
                    x.RowID == entity.ID &&
                    x.EntityName == entityName
                );

                if (level1 is not null)
                    query = query.Where(x => x.PartitionKeyLevelOneValue == level1!.Value.value &&
                        x.PartitionKeyLevelOneType == level1!.Value.type);

                if (level2 is not null)
                    query = query.Where(x => x.PartitionKeyLevelTwoValue == level2!.Value.value &&
                                           x.PartitionKeyLevelTwoType == level2!.Value.type);
                if (level3 is not null)
                    query = query.Where(x => x.PartitionKeyLevelThreeValue == level3!.Value.value &&
                                                              x.PartitionKeyLevelThreeType == level3!.Value.type);

                var log = await query.SingleOrDefaultAsync();

                if (log is not null)
                {
                    log.LastReplicationDate = DateTime.UtcNow;
                    await dbContext.SaveChangesWithoutTriggersAsync();
                }
            }
        }
    }
}


