using AutoMapper;
using EntityFrameworkCore.Triggered;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.CosmosDbReplication.Exceptions;

namespace ShiftSoftware.ShiftEntity.CosmosDbReplication.Triggers;

internal class PreventChangePartitionKeyValueTrigger<EntityType> : IBeforeSaveTrigger<EntityType>, ITriggerPriority
    where EntityType : ShiftEntity<EntityType>
{
    private readonly IServiceProvider serviceProvider;
    private readonly IMapper? mapper;
    private readonly ShiftEntityCosmosDbOptions? options;

    public PreventChangePartitionKeyValueTrigger(
        IServiceProvider serviceProvider,
        IMapper? mapper = null,
        ShiftEntityCosmosDbOptions? options = null)
    {
        this.serviceProvider = serviceProvider;
        this.mapper = mapper;
        this.options = options;
    }

    public int Priority => int.MinValue;

    public Task BeforeSave(ITriggerContext<EntityType> context, CancellationToken cancellationToken)
    {
        if (context.ChangeType != ChangeType.Modified) 
            return Task.CompletedTask;

        using var internalService = this.options?.internalServices.BuildServiceProvider();
        var cosmosDbTriggerActions = internalService?.GetService<CosmosDbTriggerReferenceOperations<EntityType>>();

        if (cosmosDbTriggerActions is null)
            return Task.CompletedTask;

        object unmodifiefItem;
        object item;

        if(cosmosDbTriggerActions.ReplicateMapping is not null)
        {
            unmodifiefItem = cosmosDbTriggerActions.ReplicateMapping(
                new EntityWrapper<EntityType>(context.UnmodifiedEntity!, serviceProvider));
            item = cosmosDbTriggerActions.ReplicateMapping(new EntityWrapper<EntityType>(context.Entity, serviceProvider));
        }
        else
        {
            unmodifiefItem = mapper!.Map(context.UnmodifiedEntity!, typeof(EntityType),
                cosmosDbTriggerActions.ReplicateComsomsDbItemType);
            item = mapper!.Map(context.Entity!, typeof(EntityType),
                cosmosDbTriggerActions.ReplicateComsomsDbItemType);
        }
        
        if(cosmosDbTriggerActions.PartitionKeyLevel1Action is not null)
            CheckPartitionKey(item, unmodifiefItem, cosmosDbTriggerActions.PartitionKeyLevel1Action);

        if (cosmosDbTriggerActions.PartitionKeyLevel2Action is not null)
            CheckPartitionKey(item, unmodifiefItem, cosmosDbTriggerActions.PartitionKeyLevel2Action);

        if (cosmosDbTriggerActions.PartitionKeyLevel3Action is not null)
            CheckPartitionKey(item, unmodifiefItem, cosmosDbTriggerActions.PartitionKeyLevel3Action);

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
