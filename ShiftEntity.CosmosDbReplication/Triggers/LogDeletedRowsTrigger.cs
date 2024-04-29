using AutoMapper;
using EntityFrameworkCore.Triggered;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.CosmosDbReplication.Exceptions;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.EFCore.Entities;

namespace ShiftSoftware.ShiftEntity.CosmosDbReplication.Triggers;

internal class LogDeletedRowsTrigger<EntityType> : IBeforeSaveTrigger<EntityType>
    where EntityType : ShiftEntity<EntityType>
{
    private readonly IServiceProvider serviceProvider;
    private readonly IMapper? mapper;
    private readonly CosmosDbTriggerReferenceOperations<EntityType>? cosmosDbTriggerActions;

    public LogDeletedRowsTrigger(
        IServiceProvider serviceProvider,
        IMapper? mapper = null,
        CosmosDbTriggerReferenceOperations<EntityType>? cosmosDbTriggerActions = null)
    {
        this.serviceProvider = serviceProvider;
        this.mapper = mapper;
        this.cosmosDbTriggerActions = cosmosDbTriggerActions;
    }

    public Task BeforeSave(ITriggerContext<EntityType> context, CancellationToken cancellationToken)
    {
        if (context.ChangeType != ChangeType.Deleted)
            return Task.CompletedTask;

        if (this.cosmosDbTriggerActions is null)
            return Task.CompletedTask;

        object item;

        if (this.cosmosDbTriggerActions.ReplicateMapping is not null)
            item = this.cosmosDbTriggerActions.ReplicateMapping(new EntityWrapper<EntityType>(context.Entity, this.serviceProvider));
        else
            item = this.mapper!.Map(context.Entity!, typeof(EntityType), this.cosmosDbTriggerActions.ReplicateComsomsDbItemType);

        var id = Convert.ToString(item.GetProperty("id"));

        (object? value, Type type, string? propertyName)? partitionKeyLevelOne = null;
        (object? value, Type type, string? propertyName)? partitionKeyLevelTwo = null;
        (object? value, Type type, string? propertyName)? partitionKeyLevelThree = null;

        if (this.cosmosDbTriggerActions.PartitionKeyLevel1Action is not null)
            partitionKeyLevelOne = this.cosmosDbTriggerActions.PartitionKeyLevel1Action(item);

        if (this.cosmosDbTriggerActions.PartitionKeyLevel2Action is not null)
            partitionKeyLevelTwo = this.cosmosDbTriggerActions.PartitionKeyLevel2Action(item);

        if (this.cosmosDbTriggerActions.PartitionKeyLevel3Action is not null)
            partitionKeyLevelThree = this.cosmosDbTriggerActions.PartitionKeyLevel3Action(item);

        var deleteRowLog = new DeletedRowLog
        {
            ContainerName = this.cosmosDbTriggerActions.ReplicateContainerId,
            RowID = long.Parse(id!)
        };

        if (partitionKeyLevelOne is not null)
        {
            deleteRowLog.PartitionKeyLevelOneType = GetPartitionKeyType(partitionKeyLevelOne.Value.type);
            deleteRowLog.PartitionKeyLevelOneValue = partitionKeyLevelOne.Value.value is null ?
                null : Convert.ToString(partitionKeyLevelOne.Value.value);
        }
        else
            deleteRowLog.PartitionKeyLevelOneType = PartitionKeyTypes.None;

        if (partitionKeyLevelTwo is not null)
        {
            deleteRowLog.PartitionKeyLevelTwoType = GetPartitionKeyType(partitionKeyLevelTwo.Value.type);
            deleteRowLog.PartitionKeyLevelTwoValue = partitionKeyLevelTwo.Value.value is null ?
                null : Convert.ToString(partitionKeyLevelTwo.Value.value);
        }
        else
            deleteRowLog.PartitionKeyLevelTwoType = PartitionKeyTypes.None;

        if (partitionKeyLevelThree is not null)
        {
            deleteRowLog.PartitionKeyLevelThreeType = GetPartitionKeyType(partitionKeyLevelThree.Value.type);
            deleteRowLog.PartitionKeyLevelThreeValue = partitionKeyLevelThree.Value.value is null ?
                null : Convert.ToString(partitionKeyLevelThree.Value.value);
        }
        else
            deleteRowLog.PartitionKeyLevelThreeType = PartitionKeyTypes.None;

        var dbContext = (ShiftDbContext)this.serviceProvider.GetRequiredService(this.cosmosDbTriggerActions.dbContextType);

        dbContext.Entry(deleteRowLog).State = Microsoft.EntityFrameworkCore.EntityState.Added;

        return Task.CompletedTask;
    }

    private PartitionKeyTypes GetPartitionKeyType(Type type)
    {
        if (type == typeof(bool?) || type == typeof(bool))
            return PartitionKeyTypes.Boolean;
        else if (type == typeof(string))
            return PartitionKeyTypes.String;
        else
            return PartitionKeyTypes.Numeric;
    }
}
