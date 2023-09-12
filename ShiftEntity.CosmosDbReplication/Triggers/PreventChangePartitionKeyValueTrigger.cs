using AutoMapper;
using EntityFrameworkCore.Triggered;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.CosmosDbReplication.Exceptions;
using ShiftSoftware.ShiftEntity.CosmosDbReplication.Extensions;

namespace ShiftSoftware.ShiftEntity.CosmosDbReplication.Triggers;

internal class PreventChangePartitionKeyValueTrigger<EntityType> : IBeforeSaveTrigger<EntityType>, ITriggerPriority
    where EntityType : ShiftEntity<EntityType>
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
                var partitionKeyAttribute = (ReplicationPartitionKeyAttribute)replicationAttribute.CosmosDbItemType.GetCustomAttributes(true)
                    .LastOrDefault(x => x as ReplicationPartitionKeyAttribute != null)!;

                if (partitionKeyAttribute is not null && partitionKeyAttribute?.PropertyName is not null)
                {
                    var property = replicationAttribute.CosmosDbItemType.GetProperty(partitionKeyAttribute.PropertyName);
                    if (property is null)
                        throw new WrongPartitionKeyNameException($"Can not find {partitionKeyAttribute.PropertyName} in the object for partition key");

                    Type propertyType = property.PropertyType;
                    if (!(propertyType == typeof(bool?) || propertyType == typeof(bool) || propertyType == typeof(string) || propertyType.IsNumericType()))
                        throw new WrongPartitionKeyTypeException("Only boolean or number or string partition key types allowed");

                    var unmodifiedEntity = context.UnmodifiedEntity;
                    var entity = context.Entity;

                    object unmodifiefItem = mapper.Map(unmodifiedEntity, typeof(EntityType), replicationAttribute.CosmosDbItemType);
                    object item = mapper.Map(entity, typeof(EntityType), replicationAttribute.CosmosDbItemType);

                    var unmodifiedPartitionKey = property.GetValue(unmodifiefItem);
                    var partitionKey = property.GetValue(item);

                    if (!Equals(unmodifiedPartitionKey, partitionKey))
                        throw new PartitionKeyChangedException("The value of partition key is changed");
                }
            }
        }

        return Task.CompletedTask;
    }
}
