using AutoMapper;
using EntityFrameworkCore.Triggered;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.CosmosDbReplication.Exceptions;
using ShiftSoftware.ShiftEntity.Model.Replication;

namespace ShiftSoftware.ShiftEntity.CosmosDbReplication.Triggers;

internal class PreventChangePartitionKeyValueTrigger<EntityType> : IBeforeSaveTrigger<EntityType>, ITriggerPriority
    where EntityType : ShiftEntityBase<EntityType>
{
    private readonly IMapper mapper;

    public PreventChangePartitionKeyValueTrigger(IMapper mapper)
    {
        this.mapper = mapper;
    }

    public int Priority => int.MinValue;

    public Task BeforeSave(ITriggerContext<EntityType> context, CancellationToken cancellationToken)
    {
        if (context.ChangeType == ChangeType.Modified)
        {
            var entityType = context.Entity.GetType();

            var replicationAttribute = (ShiftEntityReplicationAttribute)entityType.GetCustomAttributes(true).LastOrDefault(x => x as ShiftEntityReplicationAttribute != null)!;

            if (replicationAttribute != null)
            {
                var partitionKeyAttribute = (ReplicationPartitionKeyAttribute)entityType.GetCustomAttributes(true)
                    .LastOrDefault(x => x as ReplicationPartitionKeyAttribute != null)!;


                if (partitionKeyAttribute is not null)
                {

                    object unmodifiefItem = mapper.Map(context.UnmodifiedEntity, typeof(EntityType), replicationAttribute.ItemType);
                    object item = mapper.Map(context.Entity, typeof(EntityType), replicationAttribute.ItemType);

                    //Check partition key level one
                    CheckPartitionKey(item, unmodifiefItem,
                        replicationAttribute.ItemType, partitionKeyAttribute.KeyLevelOnePropertyName);

                    //Check partition key level two
                    CheckPartitionKey(item, unmodifiefItem,
                        replicationAttribute.ItemType, partitionKeyAttribute.KeyLevelTwoPropertyName);

                    //Check partition key level three
                    CheckPartitionKey(item, unmodifiefItem,
                        replicationAttribute.ItemType, partitionKeyAttribute.KeyLevelThreePropertyName);
                }
            }
        }

        return Task.CompletedTask;
    }

    private void CheckPartitionKey(object item,
        object unmodifiefItem,
        Type itemType,
        string? partitionKeyName)
    {
        if (partitionKeyName is not null)
        {
            var property = itemType.GetProperty(partitionKeyName);
            if (property is null)
                throw new WrongPartitionKeyNameException($"Can not find '{partitionKeyName}' in the '{itemType.Name}' for partition key");

            Type propertyType = property.PropertyType;
            if (!(propertyType == typeof(bool?) || propertyType == typeof(bool) || propertyType == typeof(string) || propertyType.IsNumericType()))
                throw new WrongPartitionKeyTypeException("Only boolean or number or string partition key types allowed");

            var unmodifiedPartitionKey = property.GetValue(unmodifiefItem);
            var partitionKey = property.GetValue(item);

            if (!Equals(unmodifiedPartitionKey, partitionKey))
                throw new PartitionKeyChangedException($"The value of partition key '{partitionKeyName}' is changed");
        }
    }
}
