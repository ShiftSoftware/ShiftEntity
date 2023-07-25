using AutoMapper;
using ShiftSoftware.ShiftEntity.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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
        string collectionName,
        string cosmosDbDatabaseName,
        string cosmosDbConnectionString)
    {
        var dto = mapper.Map(entity, typeof(EntityType), cosmosDbItemType);

        //Upsert to cosmosdb
    }

    internal async Task DeleteAsync(EntityType entity,
        Type cosmosDbItemType,
        string collectionName,
        string cosmosDbDatabaseName,
        string cosmosDbConnectionString)
    {
        var dto = mapper.Map(entity, typeof(EntityType), cosmosDbItemType);

        //Delete from cosmosdb
    }
}
