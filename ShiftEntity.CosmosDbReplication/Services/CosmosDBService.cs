using AutoMapper;
using EntityFrameworkCore.Triggered.Extensions;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;
using Microsoft.EntityFrameworkCore;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.CosmosDbReplication.Exceptions;
using ShiftSoftware.ShiftEntity.Model.Enums;
using ShiftSoftware.ShiftEntity.Model.Replication;
using System.ComponentModel;
using System.Dynamic;
using System.Reflection;
using System.Windows.Markup;

namespace ShiftSoftware.ShiftEntity.CosmosDbReplication.Services;

internal class CosmosDBService<EntityType>
    where EntityType : class
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
        string cosmosDbConnectionString,
        ReplicationChangeType replicationChangeType)
    {
        var dto = mapper.Map(entity, typeof(EntityType), cosmosDbItemType);

        //Upsert to cosmosdb
        using var client = await CosmosClient.CreateAndInitializeAsync(cosmosDbConnectionString,
            new List<(string, string)> { (cosmosDbDatabaseName, containerName) }, new CosmosClientOptions()
            {
                AllowBulkExecution = true
            });
        var db = client.GetDatabase(cosmosDbDatabaseName);
        var container = db.GetContainer(containerName);

        var partitionKey = GetPartitionKey(entity, dto);

        ItemResponse<object> response;

        if (partitionKey.partitionKey is null)
            response = await container.UpsertItemAsync(dto);
        else
            response = await container.UpsertItemAsync(dto, partitionKey.partitionKey.Value);

        if (response.StatusCode == System.Net.HttpStatusCode.OK ||
            response.StatusCode == System.Net.HttpStatusCode.Created ||
            response.StatusCode == System.Net.HttpStatusCode.NoContent)
        {
            int failCount = 0;

            if (replicationChangeType == ReplicationChangeType.Modified)
            {
                var referenceReplicationAttributes = (ReferenceReplicationAttribute[])entity.GetType()
                    .GetCustomAttributes(typeof(ReferenceReplicationAttribute), true);

                if (referenceReplicationAttributes is not null)
                    foreach (var referenceReplicationAttribute in referenceReplicationAttributes)
                        if (referenceReplicationAttribute is not null && referenceReplicationAttribute.ComparePropertyNames?.Count() > 0)
                            failCount = +(await UpdateReferences(db, referenceReplicationAttribute, entity, response.Resource));
            }

            if(failCount == 0)
                await UpdateLastReplicationDateAsync(entity);
        }
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="db"></param>
    /// <param name="referenceReplicationAttribute"></param>
    /// <param name="entity"></param>
    /// <param name="item"></param>
    /// <returns>Number of failed items to update</returns>
    private async Task<int> UpdateReferences(Database db, ReferenceReplicationAttribute referenceReplicationAttribute,
        EntityType entity, object item)
    {
        int failedItemsCount = 0;
        var container = db.GetContainer(referenceReplicationAttribute.ContainerName);

        var data = mapper.Map(entity, typeof(EntityType), referenceReplicationAttribute.ItemType);
        var values = GetCompareValues(referenceReplicationAttribute, data);

        var query = $"SELECT * FROM c " + (values.Count > 0 ? "WHERE " : "");
        foreach (var value in values)
        {
            query += $"c.{value.Key} = @{value.Key} AND ";
        }
        if (values.Count > 0)
            query = query.Remove(query.Length - 4);

        var parameterizedQuery = new QueryDefinition(
          query: query
        );


        foreach (var value in values)
        {
            parameterizedQuery.WithParameter($"@{value.Key}", value.Value);
        }

        var filteredFeed = container.GetItemQueryIterator<dynamic>(
            queryDefinition: parameterizedQuery
        );

        List<dynamic> itemReferences = new();

        while (filteredFeed.HasMoreResults)
        {
            itemReferences.AddRange(await filteredFeed.ReadNextAsync());
        }

        List<Task> tasks = new List<Task>();

        foreach (var itemReference in itemReferences)
        {
            var temp = mapper.Map(itemReference, typeof(DynamicObject), referenceReplicationAttribute.ItemType);

            mapper.Map(entity, temp,
                entity.GetType(), referenceReplicationAttribute.ItemType);
            var id = Convert.ToString(itemReference.id);

            tasks.Add(((Task<ItemResponse<dynamic>>)container.ReplaceItemAsync<dynamic>(temp, id,
                null, new ItemRequestOptions { IfMatchEtag = itemReference._etag }))
                .ContinueWith(x =>
                {
                    if(x.IsFaulted)
                        failedItemsCount++;
                }));
                
        }

        await Task.WhenAll(tasks);

        return failedItemsCount;
    }

    private Dictionary<string, object?> GetCompareValues(ReferenceReplicationAttribute referenceReplicationAttribute, object item)
    {
        Dictionary<string, object?> values = new Dictionary<string, object?>();

        foreach (var name in referenceReplicationAttribute.ComparePropertyNames)
        {
            Type itemType = item.GetType();

            // Find the property by name (replace "PropertyName" with your property name)
            PropertyInfo? propertyInfo = itemType.GetProperty(name);

            if (propertyInfo is not null)
                values.Add(name, propertyInfo.GetValue(item));
            else
                throw new MemberAccessException($"Can not find {name} property in the {itemType.Name}");
        }

        return values;
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

        return GetPartitionKey(new List<string> { keyLevel1, keyLevel2!, keyLevel3! }, dto);
    }

    private (PartitionKey? partitionKey,
        (string? value, PartitionKeyTypes type)? level1,
        (string? value, PartitionKeyTypes type)? level2,
        (string? value, PartitionKeyTypes type)? level3) GetPartitionKey(IEnumerable<string> keys, object dto)
    {
        var builder = new PartitionKeyBuilder();
        List<PropertyInfo?> propertyInfos = new List<PropertyInfo?>();

        foreach (var key in keys)
        {
            if (key is not null)
            {
                var propertyInfo = dto.GetType().GetProperty(key);
                if (propertyInfo is null)
                    throw new WrongPartitionKeyNameException($"Can not find {key} in the object for partition key");
                propertyInfos.Add(propertyInfo);
            }
            else
                propertyInfos.Add(null);
        }

        (string? value, PartitionKeyTypes type)? level1 = null;
        if (propertyInfos.Count > 0)
            level1 = GetPartitionKey(propertyInfos[0]!, dto, builder);

        (string? value, PartitionKeyTypes type)? level2 = null;
        if (propertyInfos.Count > 1)
            if (propertyInfos[1] is not null)
                level2 = GetPartitionKey(propertyInfos[1]!, dto, builder);

        (string? value, PartitionKeyTypes type)? level3 = null;
        if (propertyInfos.Count > 2)
            if (propertyInfos[2] is not null)
                level3 = GetPartitionKey(propertyInfos[2]!, dto, builder);

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
        var objectType = entity.GetType();
        Type genericBaseType = typeof(ShiftEntity<>);

        if (IsSubclassOfRawGeneric(genericBaseType, objectType))
        {
            foreach (var provider in dbContextProviders)
            {
                using var dbContext = provider.ProvideDbContext();

                if (dbContext.Model.GetEntityTypes().Any(x => x.ClrType == typeof(EntityType)))
                {
                    var methodInfo = objectType.GetMethod(nameof(ShiftEntity<object>.UpdateReplicationDate));
                    if (methodInfo is not null)
                        methodInfo.Invoke(entity, null);

                    //entity.UpdateReplicationDate();
                    dbContext.Attach(entity);
                    dbContext.Entry(entity).Property(nameof(ShiftEntity<object>.LastReplicationDate)).IsModified = true;
                    await dbContext.SaveChangesWithoutTriggersAsync();
                }
            }
        }
    }

    static bool IsSubclassOfRawGeneric(Type genericBaseType, Type derivedType)
    {
        while (derivedType != null && derivedType != typeof(object))
        {
            Type currentType = derivedType.IsGenericType ? derivedType.GetGenericTypeDefinition() : derivedType;
            if (genericBaseType == currentType)
                return true;
            derivedType = derivedType.BaseType;
        }
        return false;
    }

    internal async Task DeleteAsync(EntityType entity,
        Type cosmosDbItemType,
        string containerName,
        string cosmosDbDatabaseName,
        string cosmosDbConnectionString)
    {

        //Delete from cosmosdb
        using var client = await CosmosClient.CreateAndInitializeAsync(cosmosDbConnectionString,
            new List<(string, string)> { (cosmosDbDatabaseName, containerName) }, new CosmosClientOptions()
            {
                AllowBulkExecution = true
            });
        var db = client.GetDatabase(cosmosDbDatabaseName);
        var container = db.GetContainer(containerName);

        var dto = mapper.Map(entity, typeof(EntityType), cosmosDbItemType);

        var partitionKey = GetPartitionKey(entity, dto);

        ItemResponse<dynamic> response;

        if (partitionKey.partitionKey is null)
            response = await container.DeleteItemAsync<dynamic>(GetId(dto), PartitionKey.None);
        else
            response = await container.DeleteItemAsync<dynamic>(GetId(dto), partitionKey.partitionKey.Value);

        if (response.StatusCode == System.Net.HttpStatusCode.OK ||
            response.StatusCode == System.Net.HttpStatusCode.Created ||
            response.StatusCode == System.Net.HttpStatusCode.NoContent)
        {
            int failCount = 0;

            var referenceReplicationAttributes = (ReferenceReplicationAttribute[])entity.GetType()
                    .GetCustomAttributes(typeof(ReferenceReplicationAttribute), true);

            if (referenceReplicationAttributes is not null)
                foreach (var referenceReplicationAttribute in referenceReplicationAttributes)
                    if (referenceReplicationAttribute is not null && referenceReplicationAttribute.ComparePropertyNames?.Count() > 0)
                        failCount = +(await DeleteReferences(db, referenceReplicationAttribute, entity, response.Resource));

            if (failCount == 0)
                await UpdateDeleteRowLogLastReplicationDateAsync(entity, cosmosDbItemType, containerName,
                    partitionKey.level1, partitionKey.level2, partitionKey.level3);
        }
    }

    private async Task UpdateDeleteRowLogLastReplicationDateAsync(EntityType entity,
        Type cosmosDbItemType,
        string containerName,
        (string? value, PartitionKeyTypes type)? level1,
        (string? value, PartitionKeyTypes type)? level2,
        (string? value, PartitionKeyTypes type)? level3)
    {
        var dto = mapper.Map(entity, typeof(EntityType), cosmosDbItemType);
        long id = 0;
        long.TryParse(GetId(dto), out id);

        foreach (var provider in dbContextProviders)
        {
            using var dbContext = provider.ProvideDbContext();

            if (dbContext.Model.GetEntityTypes().Any(x => x.ClrType == typeof(EntityType)))
            {
                var query = dbContext.DeletedRowLogs.Where(x =>
                    x.RowID == id &&
                    x.ContainerName == containerName
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
                    dbContext.DeletedRowLogs.Remove(log);
                    await dbContext.SaveChangesWithoutTriggersAsync();
                }
            }
        }
    }

    private async Task<int> DeleteReferences(Database db, ReferenceReplicationAttribute referenceReplicationAttribute,
        EntityType entity, object item)
    {
        int failedItemsCount = 0;
        var container = db.GetContainer(referenceReplicationAttribute.ContainerName);

        var data = mapper.Map(entity, typeof(EntityType), referenceReplicationAttribute.ItemType);
        var values = GetCompareValues(referenceReplicationAttribute, data);

        var query = $"SELECT * FROM c " + (values.Count > 0 ? "WHERE " : "");
        foreach (var value in values)
        {
            query += $"c.{value.Key} = @{value.Key} AND ";
        }
        if (values.Count > 0)
            query = query.Remove(query.Length - 4);

        var parameterizedQuery = new QueryDefinition(
          query: query
        );


        foreach (var value in values)
        {
            parameterizedQuery.WithParameter($"@{value.Key}", value.Value);
        }

        var filteredFeed = container.GetItemQueryIterator<dynamic>(
            queryDefinition: parameterizedQuery
        );

        List<dynamic> itemReferences = new();
        var iterator = container.GetItemLinqQueryable<dynamic>().ToFeedIterator();

        while (filteredFeed.HasMoreResults)
        {
            itemReferences.AddRange(await filteredFeed.ReadNextAsync());
        }

        var containerResponse = await container.ReadContainerAsync();
        var keys = containerResponse.Resource.PartitionKeyPaths.Select(x => x.Substring(1));

        List<Task> tasks = new List<Task>();

        foreach (var itemReference in itemReferences)
        {
            var temp = mapper.Map(itemReference, typeof(DynamicObject), referenceReplicationAttribute.ItemType);

            mapper.Map(entity, temp,
                entity.GetType(), referenceReplicationAttribute.ItemType);
            var id = Convert.ToString(itemReference.id);

            var partitionKey = GetPartitionKey(keys, (object)temp);

            tasks.Add(((Task<ItemResponse<dynamic>>)container.DeleteItemAsync<dynamic>(id, partitionKey.partitionKey.Value,
                new ItemRequestOptions { IfMatchEtag = itemReference._etag }))
                .ContinueWith(x =>
                {
                    if (x.IsFaulted)
                        failedItemsCount++;
                }));

        }

        await Task.WhenAll(tasks);

        return failedItemsCount;
    }

    private string? GetId(object obj)
    {
        // Get the type of the object
        Type objectType = obj.GetType();

        // Find the property by name (replace "PropertyName" with your property name)
        PropertyInfo? propertyInfo = objectType.GetProperty("id");

        if (propertyInfo is not null)
            return Convert.ToString(propertyInfo.GetValue(obj));
        else
            throw new MemberAccessException($"Can not find id property in the {objectType.Name}");
    }
}


