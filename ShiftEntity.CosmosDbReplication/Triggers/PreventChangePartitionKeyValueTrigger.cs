using AutoMapper;
using EntityFrameworkCore.Triggered;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.CosmosDbReplication.Exceptions;
using ShiftSoftware.ShiftEntity.Model.Replication;

namespace ShiftSoftware.ShiftEntity.CosmosDbReplication.Triggers;

internal class PreventChangePartitionKeyValueTrigger<EntityType> : IBeforeSaveTrigger<EntityType>, ITriggerPriority
    where EntityType : ShiftEntity<EntityType>
{
    private readonly IServiceProvider serviceProvider;
    private readonly IMapper? mapper;
    private readonly CosmosDbTriggerReferenceOperations<EntityType>? cosmosDbTriggerActions;

    public PreventChangePartitionKeyValueTrigger(
        IServiceProvider serviceProvider,
        IMapper? mapper = null,
        CosmosDbTriggerReferenceOperations<EntityType>? cosmosDbTriggerActions = null)
    {
        this.serviceProvider = serviceProvider;
        this.mapper = mapper;
        this.cosmosDbTriggerActions = cosmosDbTriggerActions;
    }

    public int Priority => int.MinValue;

    public Task BeforeSave(ITriggerContext<EntityType> context, CancellationToken cancellationToken)
    {
        if (context.ChangeType != ChangeType.Modified) 
            return Task.CompletedTask;

        if(this.cosmosDbTriggerActions is null)
            return Task.CompletedTask;

        object unmodifiefItem;
        object item;

        if(this.cosmosDbTriggerActions.ReplicateMipping is not null)
        {
            unmodifiefItem = this.cosmosDbTriggerActions.ReplicateMipping(
                new EntityWrapper<EntityType>(context.UnmodifiedEntity!, this.serviceProvider));
            item = this.cosmosDbTriggerActions.ReplicateMipping(new EntityWrapper<EntityType>(context.Entity, this.serviceProvider));
        }
        else
        {
            unmodifiefItem = this.mapper!.Map(context.UnmodifiedEntity!, typeof(EntityType),
                this.cosmosDbTriggerActions.ReplicateComsomsDbItemType);
            item = this.mapper!.Map(context.Entity!, typeof(EntityType),
                this.cosmosDbTriggerActions.ReplicateComsomsDbItemType);
        }
        
        if(this.cosmosDbTriggerActions.PartitionKeyLevel1Action is not null)
            CheckPartitionKey(item, unmodifiefItem, this.cosmosDbTriggerActions.PartitionKeyLevel1Action);

        if (this.cosmosDbTriggerActions.PartitionKeyLevel2Action is not null)
            CheckPartitionKey(item, unmodifiefItem, this.cosmosDbTriggerActions.PartitionKeyLevel2Action);

        if (this.cosmosDbTriggerActions.PartitionKeyLevel3Action is not null)
            CheckPartitionKey(item, unmodifiefItem, this.cosmosDbTriggerActions.PartitionKeyLevel3Action);

        return Task.CompletedTask;
    }

    private void CheckPartitionKey(object item, object unmodifiedItem,
        Func<object, (object? value, Type type, string? propertyName)?> partitionKeyAction)
    {
        var unmodifiedResult = partitionKeyAction(unmodifiedItem);
        var result = partitionKeyAction(item);

        if (result?.value?.Equals(unmodifiedResult?.value) == false)
            throw new PartitionKeyChangedException($"The value of partition key '{result?.propertyName}' is changed");
    }
}
