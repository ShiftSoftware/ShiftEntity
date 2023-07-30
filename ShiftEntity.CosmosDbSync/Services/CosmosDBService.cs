using AutoMapper;
using Microsoft.Azure.Cosmos;
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

    public CosmosDBService(IMapper mapper)
    {
        this.mapper = mapper;
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

        Type objectType = item.GetType();
        PropertyInfo? idProperty = objectType.GetProperty(nameof(ShiftEntity.Model.Dtos.ShiftEntityDTOBase.ID), BindingFlags.Public | BindingFlags.Instance);

        if (idProperty == null || idProperty?.PropertyType != typeof(string))
            throw new ArgumentException($"The cosmosDbItemType dose not contain string ID");

        string id = (string)idProperty.GetValue(item)!;

        await container.UpsertItemAsync(new { id = id, item = item });
    }

    internal async Task DeleteAsync(EntityType entity,
        Type cosmosDbItemType,
        string containerName,
        string cosmosDbDatabaseName,
        string cosmosDbConnectionString)
    {
        var item = mapper.Map(entity, typeof(EntityType), cosmosDbItemType);

        //Delete from cosmosdb
        using CosmosClient client = new(cosmosDbConnectionString);
        var db = client.GetDatabase(cosmosDbDatabaseName);
        var container = db.GetContainer(containerName);

        Type objectType = item.GetType();
        PropertyInfo idProperty = objectType.GetProperty(nameof(ShiftEntity.Model.Dtos.ShiftEntityDTOBase.ID), BindingFlags.Public | BindingFlags.Instance);
        string id = (string)idProperty.GetValue(item)!;

        await container.DeleteItemAsync<dynamic>(id, PartitionKey.None);
    }
}
