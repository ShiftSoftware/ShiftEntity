using EntityFrameworkCore.Triggered;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.CosmosDbReplication.Services;
using ShiftSoftware.ShiftEntity.Model.Replication;

namespace ShiftSoftware.ShiftEntity.CosmosDbReplication.Triggers;


internal class ReplicateToCosmosDbAfterSaveTrigger<EntityType> : IAfterSaveTrigger<EntityType>, ITriggerPriority
    where EntityType : ShiftEntity<EntityType>
{
    private readonly ShiftEntityCosmosDbOptions options;
    private readonly CosmosDBService<EntityType> cosmosDBService;
    private readonly IShiftEntityPrepareForReplicationAsync<EntityType> prepareForReplication;

    public ReplicateToCosmosDbAfterSaveTrigger(
        ShiftEntityCosmosDbOptions options,
        CosmosDBService<EntityType> cosmosDBService,
        IShiftEntityPrepareForReplicationAsync<EntityType> prepareForReplication)
    {
        this.options = options;
        this.cosmosDBService = cosmosDBService;
        this.prepareForReplication = prepareForReplication;
    }

    //Make the trigger excute the last one
    public int Priority => int.MaxValue;

    public async Task AfterSave(ITriggerContext<EntityType> context, CancellationToken cancellationToken)
    {
        var entityType = context.Entity.GetType();

        var replicationAttribute = (ShiftEntityReplicationAttribute)entityType.GetCustomAttributes(true).LastOrDefault(x => x as ShiftEntityReplicationAttribute != null)!;

        if (replicationAttribute != null)
        {
            _ = Task.Run(async () =>
            {
                var configurations = GetConfigurations(replicationAttribute, entityType.Name);
                var entity = await prepareForReplication.PrepareForReplicationAsync(context.Entity, ConvertChangeTypeToReplicationChangeType(context.ChangeType));

                if (context.ChangeType == ChangeType.Added || context.ChangeType == ChangeType.Modified)
                    _ = cosmosDBService.UpsertAsync(entity, replicationAttribute.ItemType,
                        configurations.collection, configurations.databaseName, configurations.connectionString);
                else if (context.ChangeType == ChangeType.Deleted)
                    _ = cosmosDBService.DeleteAsync(entity, replicationAttribute.ItemType,
                        configurations.collection, configurations.databaseName, configurations.connectionString);
            });
        }
    }

    private (string collection, string connectionString, string databaseName) GetConfigurations
        (ShiftEntityReplicationAttribute replicationAttribute, string entityName)
    {
        string container;
        string connectionString;
        string databaseName;
        CosmosDBAccount? account;

        container = replicationAttribute.ContainerName ?? entityName;

        if (replicationAttribute.AccountName is null)
        {
            if (!options.Accounts.Any(x => x.IsDefault))
                throw new ArgumentException("No account specified");
            else
            {
                account = options.Accounts.FirstOrDefault(x => x.IsDefault);
                connectionString = account!.ConnectionString;
            }
        }
        else
        {
            account = options.Accounts.FirstOrDefault(x => x.Name.ToLower() == replicationAttribute.AccountName.ToLower());
            if (account is null)
                throw new ArgumentException($"Can not find any account by name '{replicationAttribute.AccountName}'");
            else
                connectionString = account.ConnectionString;
        }

        if (replicationAttribute.DatabaseName is null && account.DefaultDatabaseName is null)
            throw new ArgumentException("No database specified");
        else
            databaseName = replicationAttribute.DatabaseName! ?? account.DefaultDatabaseName!;

        return (container, connectionString, databaseName);
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