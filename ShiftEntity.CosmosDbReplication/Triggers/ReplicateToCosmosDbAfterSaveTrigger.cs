using EntityFrameworkCore.Triggered;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.CosmosDbReplication.Services;
using ShiftSoftware.ShiftEntity.Model.Enums;
using ShiftSoftware.ShiftEntity.Model.Replication;

namespace ShiftSoftware.ShiftEntity.CosmosDbReplication.Triggers;


//Registered as an open generic for every entity type; the DI container only materializes it for types that
//satisfy the constraints (it already skips the base/interface closures Triggered probes), so saves of entities
//that don't implement IShiftEntityReplication never enter this trigger — they can't be set up for replication
//in the first place. The operations-null check below covers the remaining case: an entity that implements the
//interface but has no SetUpReplication registration.
internal class ReplicateToCosmosDbAfterSaveTrigger<EntityType> : IAfterSaveTrigger<EntityType>, ITriggerPriority
    where EntityType : ShiftEntity<EntityType>, IShiftEntityReplication
{
    private readonly IServiceProvider serviceProvider;
    private readonly IShiftEntityPrepareForReplicationAsync<EntityType>? prepareForReplication;
    private readonly ILogger<ReplicateToCosmosDbAfterSaveTrigger<EntityType>> logger;
    private readonly ShiftEntityCosmosDbOptions? options;

    public ReplicateToCosmosDbAfterSaveTrigger(
        IServiceProvider serviceProvider,
        ILogger<ReplicateToCosmosDbAfterSaveTrigger<EntityType>> logger,
        ShiftEntityCosmosDbOptions? options = null,
        IShiftEntityPrepareForReplicationAsync<EntityType>? prepareForReplication = null)
    {
        this.serviceProvider = serviceProvider;
        this.prepareForReplication = prepareForReplication;
        this.logger = logger;
        this.options = options;
    }

    //Make the trigger excute the last one
    public int Priority => int.MaxValue;

    public async Task AfterSave(ITriggerContext<EntityType> context, CancellationToken cancellationToken)
    {
        var entityType = context.Entity.GetType();

        if (this.options is null)
            return;

        //The framework's delete is always a soft delete (an update), so there is no delete replication. A raw EF
        //Remove on a replicated entity is NOT mirrored to Cosmos.
        if (context.ChangeType == ChangeType.Deleted)
            return;

        using var internalService = this.options.internalServices.BuildServiceProvider();
        var operations = internalService.GetService<CosmosDbTriggerReferenceOperations<EntityType>>();

        if (operations is null)
            return;

        var serviceProvider = this.serviceProvider.CreateAsyncScope().ServiceProvider;

        var entity = context.Entity;
        var changeType = ConvertChangeTypeToReplicationChangeType(context.ChangeType);

        if (prepareForReplication is not null)
            entity = await prepareForReplication.PrepareForReplicationAsync(context.Entity, changeType);

        this.logger.LogInformation("CosmosDB Syncing is starting to Sync {entityType} - With ID: {entityID}", entity.GetType(), entity.ID);
        _ = Task.Run(async () =>
        {
            this.logger.LogInformation("CosmosDB Syncing Task is Running {entityType} - With ID: {entityID}", entity.GetType(), entity.ID);

            await operations.RunAsync(entity, serviceProvider, context.ChangeType);
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