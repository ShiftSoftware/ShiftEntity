using AutoMapper;
using EntityFrameworkCore.Triggered;
using ShiftSoftware.EFCore.SqlServer;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.CosmosDbSync.Exceptions;
using ShiftSoftware.ShiftEntity.CosmosDbSync.Extensions;
using ShiftSoftware.ShiftEntity.EFCore.SqlServer.Entities;

namespace ShiftSoftware.ShiftEntity.CosmosDbSync.Triggers;

internal class LogDeletedRowsTrigger<EntityType> : IBeforeSaveTrigger<EntityType>
    where EntityType : ShiftEntity<EntityType>
{
    private readonly IEnumerable<ShiftDbContext> dbContexts;
    private readonly IMapper mapper;

    public LogDeletedRowsTrigger(IEnumerable<ShiftDbContext> dbContexts, IMapper mapper)
    {
        this.dbContexts = dbContexts;
        this.mapper = mapper;
    }

    public Task BeforeSave(ITriggerContext<EntityType> context, CancellationToken cancellationToken)
    {
        if (context.ChangeType == ChangeType.Deleted)
        {
            var entityType = context.Entity.GetType();

            var syncAttribute = (ShiftEntitySyncAttribute)entityType.GetCustomAttributes(true).LastOrDefault(x => x as ShiftEntitySyncAttribute != null)!;

            if (syncAttribute != null)
            {
                var partitionKeyAttribute = (SyncPartitionKeyAttribute)syncAttribute.CosmosDbItemType.GetCustomAttributes(true)
                    .LastOrDefault(x => x as SyncPartitionKeyAttribute != null)!;

                var entity = context.Entity;
                object partitionKey = null;
                PartitionKeyTypes partitionKeyType = PartitionKeyTypes.None;

                if (partitionKeyAttribute is not null && partitionKeyAttribute?.PropertyName is not null)
                {
                    var property = syncAttribute.CosmosDbItemType.GetProperty(partitionKeyAttribute.PropertyName);
                    if (property is null)
                        throw new WrongPartitionKeyNameException($"Can not find {partitionKeyAttribute.PropertyName} in the object");

                    Type propertyType = property.PropertyType;
                    if (!(propertyType == typeof(bool?) || propertyType == typeof(bool) || propertyType == typeof(string) || propertyType.IsNumericType()))
                        throw new WrongPartitionKeyTypeException("Only boolean or number or string partition key types allowed");

                    

                    object item = mapper.Map(entity, typeof(EntityType), syncAttribute.CosmosDbItemType);

                    partitionKey = property.GetValue(item);

                    if (propertyType == typeof(bool?) || propertyType == typeof(bool))
                        partitionKeyType = PartitionKeyTypes.Boolean;
                    else if (propertyType == typeof(string))
                        partitionKeyType = PartitionKeyTypes.String;
                    else
                        partitionKeyType = PartitionKeyTypes.Numeric;
                }

                var deleteRowLog = new DeletedRowLog
                {
                    EntityName = context.Entity.GetType().Name,
                    RowID = entity.ID,
                    PartitionKeyType = partitionKeyType,
                    PartitionKeyValue = partitionKey?.ToString(),
                };

                foreach (var dbContext in dbContexts)
                
                    if (dbContext.Model.GetEntityTypes().Any(x => x.ClrType == typeof(EntityType)))
                    {
                        dbContext.Entry(deleteRowLog).State = Microsoft.EntityFrameworkCore.EntityState.Added;
                    }
                }
            }

        return Task.CompletedTask;
    }
}
