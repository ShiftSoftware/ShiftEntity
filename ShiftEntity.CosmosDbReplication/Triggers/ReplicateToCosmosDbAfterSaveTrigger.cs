using EntityFrameworkCore.Triggered;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.CosmosDbReplication.Services;
using ShiftSoftware.ShiftEntity.Model.Enums;
using ShiftSoftware.ShiftEntity.Model.Replication;

namespace ShiftSoftware.ShiftEntity.CosmosDbReplication.Triggers;


internal class ReplicateToCosmosDbAfterSaveTrigger<EntityType> : IAfterSaveTrigger<EntityType>, ITriggerPriority
    where EntityType : ShiftEntity<EntityType>
{
    private readonly CosmosDbTriggerReferenceOperations<EntityType>? cosmosDbTriggerActions;
    private readonly IServiceProvider serviceProvider;
    private readonly IShiftEntityPrepareForReplicationAsync<EntityType>? prepareForReplication;
    private readonly ILogger<ReplicateToCosmosDbAfterSaveTrigger<EntityType>> logger;

    public ReplicateToCosmosDbAfterSaveTrigger(
        IServiceProvider serviceProvider,
        ILogger<ReplicateToCosmosDbAfterSaveTrigger<EntityType>> logger,
        CosmosDbTriggerReferenceOperations<EntityType>? cosmosDbTriggerActions = null,
        IShiftEntityPrepareForReplicationAsync<EntityType>? prepareForReplication = null)
    {
        this.cosmosDbTriggerActions = cosmosDbTriggerActions;
        this.serviceProvider = serviceProvider;
        this.prepareForReplication = prepareForReplication;
        this.logger = logger;
    }

    //Make the trigger excute the last one
    public int Priority => int.MaxValue;

    public async Task AfterSave(ITriggerContext<EntityType> context, CancellationToken cancellationToken)
    {
        var entityType = context.Entity.GetType();

        if (this.cosmosDbTriggerActions is not null)
        {
            var serviceProvider = this.serviceProvider.CreateAsyncScope().ServiceProvider;

            var entity = context.Entity;
            var changeType = ConvertChangeTypeToReplicationChangeType(context.ChangeType);

            if (prepareForReplication is not null)
                entity = await prepareForReplication.PrepareForReplicationAsync(context.Entity, changeType);

            this.logger.LogInformation("CosmosDB Syncing is starting to Sync {entityType} - With ID: {entityID}", entity.GetType(), entity.ID);
            _ = Task.Run(async () =>
            {
                this.logger.LogInformation("CosmosDB Syncing Task is Running {entityType} - With ID: {entityID}", entity.GetType(), entity.ID);

                await this.cosmosDbTriggerActions.RunAsync(entity, serviceProvider, context.ChangeType);
            }).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    this.logger.LogError("CosmosDB Syncing Failed for {entityType} - With ID: {entityID} with Exception: {exception}", entity.GetType(), entity.ID, t.Exception);
                }
                else
                {
                    this.logger.LogInformation("CosmosDB Syncing Succeeded for {entityType} - With ID: {entityID}", entity.GetType(), entity.ID);
                }
            });
        }
    }

    private ReplicationChangeType ConvertChangeTypeToReplicationChangeType(ChangeType changeType)
    {
        if (changeType == ChangeType.Added)
            return ReplicationChangeType.Added;
        else if (changeType == ChangeType.Deleted)
            return ReplicationChangeType.Deleted;
        else if (changeType == ChangeType.Modified)
            return ReplicationChangeType.Modified;
        else
            throw new ArgumentException($"Change type {changeType} is not supported");
    }
}