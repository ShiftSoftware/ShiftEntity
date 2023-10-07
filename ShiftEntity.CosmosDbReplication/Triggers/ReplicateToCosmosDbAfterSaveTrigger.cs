using EntityFrameworkCore.Triggered;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.CosmosDbReplication.Services;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Model.Replication;

namespace ShiftSoftware.ShiftEntity.CosmosDbReplication.Triggers;


internal class ReplicateToCosmosDbAfterSaveTrigger<EntityType> : IAfterSaveTrigger<EntityType>, ITriggerPriority
    where EntityType : ShiftEntityBase<EntityType>
{
    private readonly ShiftEntityCosmosDbOptions options;
    private readonly CosmosDBService<EntityType> cosmosDBService;
    private readonly IShiftEntityPrepareForReplicationAsync<EntityType>? prepareForReplication;

    public ReplicateToCosmosDbAfterSaveTrigger(
        ShiftEntityCosmosDbOptions options,
        CosmosDBService<EntityType> cosmosDBService,
        IShiftEntityPrepareForReplicationAsync<EntityType>? prepareForReplication = null)
    {
        this.options = options;
        this.cosmosDBService = cosmosDBService;
        this.prepareForReplication = prepareForReplication;
    }

    //Make the trigger excute the last one
    public int Priority => int.MaxValue;

    public Task AfterSave(ITriggerContext<EntityType> context, CancellationToken cancellationToken)
    {
        var entityType = context.Entity.GetType();

        var replicationAttribute = (ShiftEntityReplicationAttribute)entityType.GetCustomAttributes(true).LastOrDefault(x => x as ShiftEntityReplicationAttribute != null)!;

        if (replicationAttribute != null)
        {
            Task.Run(async () =>
             {
                 var configurations = replicationAttribute.GetConfigurations(options.Accounts, entityType.Name);

                 var entity = context.Entity;
                 if(prepareForReplication is not null)
                    entity = await prepareForReplication.PrepareForReplicationAsync(context.Entity,
                        ConvertChangeTypeToReplicationChangeType(context.ChangeType));

                 if (context.ChangeType == ChangeType.Added || context.ChangeType == ChangeType.Modified)
                     await cosmosDBService.UpsertAsync(entity, replicationAttribute.ItemType,
                         configurations.collection, configurations.databaseName, configurations.connectionString);
                 else if (context.ChangeType == ChangeType.Deleted)
                     await cosmosDBService.DeleteAsync(entity, replicationAttribute.ItemType,
                         configurations.collection, configurations.databaseName, configurations.connectionString);
             }).ContinueWith(t => Console.Error.WriteLine(t.Exception),
                  TaskContinuationOptions.OnlyOnFaulted);
        }
        
        return Task.CompletedTask;
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