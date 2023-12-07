using EntityFrameworkCore.Triggered;
using Microsoft.Extensions.DependencyInjection;
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

    public ReplicateToCosmosDbAfterSaveTrigger(
        IServiceProvider serviceProvider,
        CosmosDbTriggerReferenceOperations<EntityType>? cosmosDbTriggerActions = null,
        IShiftEntityPrepareForReplicationAsync<EntityType>? prepareForReplication = null)
    {
        this.cosmosDbTriggerActions = cosmosDbTriggerActions;
        this.serviceProvider = serviceProvider;
        this.prepareForReplication = prepareForReplication;
    }

    //Make the trigger excute the last one
    public int Priority => int.MaxValue;

    public async Task AfterSave(ITriggerContext<EntityType> context, CancellationToken cancellationToken)
    {
        var entityType = context.Entity.GetType();

        if (this.cosmosDbTriggerActions is not null)
        {
            var serviceProvider = this.serviceProvider.CreateAsyncScope().ServiceProvider;

            _ = Task.Run(async () =>
            {
                await this.cosmosDbTriggerActions.RunAsync(context.Entity, serviceProvider, context.ChangeType);
            }).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    //Console.Error.WriteLine("----------------------------------------------------------------------------------------------");
                    Console.ForegroundColor = ConsoleColor.Red; // Set text color to red
                    Console.Error.Write("fail: ");
                    Console.ResetColor();
                    Console.Error.WriteLine(t.Exception);
                    //Console.Error.WriteLine("----------------------------------------------------------------------------------------------");
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