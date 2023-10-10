using AutoMapper;
using EntityFrameworkCore.Triggered;
using Microsoft.Azure.Cosmos.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.CosmosDbReplication.Exceptions;
using ShiftSoftware.ShiftEntity.CosmosDbReplication.Extensions;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.EFCore.Entities;
using ShiftSoftware.ShiftEntity.Model.Replication;

namespace ShiftSoftware.ShiftEntity.CosmosDbReplication.Triggers;

internal class LogDeletedRowsTrigger<EntityType> : IBeforeSaveTrigger<EntityType>
    where EntityType : ShiftEntityBase<EntityType>
{
    private readonly IEnumerable<ShiftDbContext> dbContexts;
    private readonly IMapper mapper;
    private readonly ShiftEntityCosmosDbOptions options;
    private readonly IServiceProvider serviceProvider;

    public LogDeletedRowsTrigger(IEnumerable<ShiftDbContext> dbContexts, IMapper mapper, 
        ShiftEntityCosmosDbOptions options, IServiceProvider serviceProvider)
    {
        this.dbContexts = dbContexts;
        this.mapper = mapper;
        this.options = options;
        this.serviceProvider = serviceProvider;
    }

    public Task BeforeSave(ITriggerContext<EntityType> context, CancellationToken cancellationToken)
    {
        if (context.ChangeType == ChangeType.Deleted)
        {
            var entityType = context.Entity.GetType();

            var replicationAttribute = (ShiftEntityReplicationAttribute)entityType.GetCustomAttributes(true).LastOrDefault(x => x as ShiftEntityReplicationAttribute != null)!;

            if (true)
            {
                var partitionKeyAttribute = (ReplicationPartitionKeyAttribute)entityType.GetCustomAttributes(true)
                    .LastOrDefault(x => x as ReplicationPartitionKeyAttribute != null)!;

                var entity = context.Entity;
                (string? value, PartitionKeyTypes type) partitionKeyLevelOne = (null, PartitionKeyTypes.None);
                (string? value, PartitionKeyTypes type) partitionKeyLevelTwo = (null, PartitionKeyTypes.None);
                (string? value, PartitionKeyTypes type) partitionKeyLevelThree = (null, PartitionKeyTypes.None);

                var id = "";

                if (partitionKeyAttribute is not null)
                {
                    object item = mapper.Map(entity, typeof(EntityType), replicationAttribute.ItemType);

                    id = Convert.ToString(item.GetProperty("id"));

                    partitionKeyLevelOne = GetPatitionKey(replicationAttribute.ItemType, item, partitionKeyAttribute.KeyLevelOnePropertyName);
                    partitionKeyLevelTwo = GetPatitionKey(replicationAttribute.ItemType, item, partitionKeyAttribute.KeyLevelTwoPropertyName);
                    partitionKeyLevelThree = GetPatitionKey(replicationAttribute.ItemType, item, partitionKeyAttribute.KeyLevelThreePropertyName);
                }

                var deleteRowLog = new DeletedRowLog
                {
                    EntityName = context.Entity.GetType().Name,
                    RowID = long.Parse(id!),
                    PartitionKeyLevelOneType = partitionKeyLevelOne.type,
                    PartitionKeyLevelOneValue = partitionKeyLevelOne.value,
                    PartitionKeyLevelTwoType = partitionKeyLevelTwo.type,
                    PartitionKeyLevelTwoValue = partitionKeyLevelTwo.value,
                    PartitionKeyLevelThreeType = partitionKeyLevelThree.type,
                    PartitionKeyLevelThreeValue = partitionKeyLevelThree.value,
                };

                foreach (var dbType in options.ShiftDbContextStorage)
                {
                    var dbContext = (DbContext)serviceProvider.GetRequiredService(dbType.ShiftDbContextType);
                        if (dbContext.Model.GetEntityTypes().Any(x => x.ClrType == typeof(EntityType)))
                            dbContext.Entry(deleteRowLog).State = Microsoft.EntityFrameworkCore.EntityState.Added;
                }
            }
        }

        return Task.CompletedTask;
    }

    private (string? value, PartitionKeyTypes type) GetPatitionKey(Type itemType, object item, string? partitionKeyName)
    {
        if (partitionKeyName is null)
            return (null, PartitionKeyTypes.None);

        var property = itemType.GetProperty(partitionKeyName);
        if (property is null)
            throw new WrongPartitionKeyNameException($"Can not find {partitionKeyName} in the {itemType.Name}");

        Type propertyType = property.PropertyType;
        if (!(propertyType == typeof(bool?) || propertyType == typeof(bool) || propertyType == typeof(string) || propertyType.IsNumericType()))
            throw new WrongPartitionKeyTypeException("Only boolean or number or string partition key types allowed");

        PartitionKeyTypes partitionKeyType;
        var partitionKeyValue = property.GetValue(item);

        if (propertyType == typeof(bool?) || propertyType == typeof(bool))
            partitionKeyType = PartitionKeyTypes.Boolean;
        else if (propertyType == typeof(string))
            partitionKeyType = PartitionKeyTypes.String;
        else
            partitionKeyType = PartitionKeyTypes.Numeric;

        return (partitionKeyValue?.ToString(), partitionKeyType);
    }
}
