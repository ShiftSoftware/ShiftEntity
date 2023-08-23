using AutoMapper;
using EntityFrameworkCore.Triggered;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.CosmosDbSync.Exceptions;
using ShiftSoftware.ShiftEntity.CosmosDbSync.Extensions;
using ShiftSoftware.ShiftEntity.CosmosDbSync.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.CosmosDbSync.Triggers;

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
        if (context.ChangeType==ChangeType.Modified)
        {
            var entityType = context.Entity.GetType();

            var syncAttribute = (ShiftEntitySyncAttribute)entityType.GetCustomAttributes(true).LastOrDefault(x => x as ShiftEntitySyncAttribute != null)!;

            if (syncAttribute != null)
            {
                var partitionKeyAttribute = (SyncPartitionKeyAttribute)syncAttribute.CosmosDbItemType.GetCustomAttributes(true)
                    .LastOrDefault(x => x as SyncPartitionKeyAttribute != null)!;

                if (partitionKeyAttribute is not null && partitionKeyAttribute?.PropertyName is not null)
                {
                    var property = syncAttribute.CosmosDbItemType.GetProperty(partitionKeyAttribute.PropertyName);
                    if (property is null)
                        throw new WrongPartitionKeyNameException($"Can not find {partitionKeyAttribute.PropertyName} in the object");

                    Type propertyType = property.PropertyType;
                    if (!(propertyType == typeof(bool?) || propertyType == typeof(bool) || propertyType == typeof(string) || propertyType.IsNumericType()))
                        throw new WrongPartitionKeyTypeException("Only boolean or number or string partition key types allowed");

                    var unmodifiedEntity = context.UnmodifiedEntity;
                    var entity = context.Entity;

                    object unmodifiefItem = mapper.Map(unmodifiedEntity, typeof(EntityType), syncAttribute.CosmosDbItemType);
                    object item = mapper.Map(entity, typeof(EntityType), syncAttribute.CosmosDbItemType);

                    var unmodifiedPartitionKey = property.GetValue(unmodifiefItem);
                    var partitionKey = property.GetValue(item);

                    if (!object.Equals(unmodifiedPartitionKey, partitionKey))
                        throw new PartitionKeyChangedException("The value of partition key is changed");
                }
            }
        }

        return Task.CompletedTask;
    }
}
