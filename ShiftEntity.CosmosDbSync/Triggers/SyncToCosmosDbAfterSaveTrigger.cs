using EntityFrameworkCore.Triggered;
using Microsoft.EntityFrameworkCore.Metadata.Internal;
using Microsoft.EntityFrameworkCore;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.CosmosDbSync.Services;
using System.ComponentModel.Design;

namespace ShiftSoftware.ShiftEntity.CosmosDbSync.Triggers;


internal class SyncToCosmosDbAfterSaveTrigger<EntityType> : IAfterSaveTrigger<EntityType>, ITriggerPriority
    where EntityType : ShiftEntity<EntityType>
{
    private readonly ShiftEntityCosmosDbOptions options;
    private readonly CosmosDBService<EntityType> cosmosDBService;
    private readonly IShiftEntityPrepareForReplicationAsync<EntityType> prepareForSync;

    public SyncToCosmosDbAfterSaveTrigger(
        ShiftEntityCosmosDbOptions options,
        CosmosDBService<EntityType> cosmosDBService,
        IShiftEntityPrepareForReplicationAsync<EntityType> prepareForSync)
    {
        this.options = options;
        this.cosmosDBService = cosmosDBService;
        this.prepareForSync = prepareForSync;
    }

    //Make the trigger excute the last one
    public int Priority => int.MaxValue;

    public async Task AfterSave(ITriggerContext<EntityType> context, CancellationToken cancellationToken)
    {
        var entityType = context.Entity.GetType();
        
        var syncAttribute = (ShiftEntitySyncAttribute)entityType.GetCustomAttributes(true).LastOrDefault(x => x as ShiftEntitySyncAttribute != null)!;

        if (syncAttribute != null)
        {
            _ = Task.Run(async () =>
            {
                var configurations = GetConfigurations(syncAttribute, entityType.Name);
                var entity = await prepareForSync.PrepareForSyncAsync(context.Entity, ConvertChangeTypeToSyncChangeType(context.ChangeType));

                if (context.ChangeType == ChangeType.Added || context.ChangeType == ChangeType.Modified)
                    _ = cosmosDBService.UpsertAsync(entity, syncAttribute.CosmosDbItemType,
                        configurations.collection, configurations.databaseName, configurations.connectionString);
                else if (context.ChangeType == ChangeType.Deleted)
                    _ = cosmosDBService.DeleteAsync(entity, syncAttribute.CosmosDbItemType,
                        configurations.collection, configurations.databaseName, configurations.connectionString);
            });
        }
    }

    private (string collection, string connectionString, string databaseName) GetConfigurations
        (ShiftEntitySyncAttribute syncAttribute, string entityName)
    {
        string container;
        string connectionString;
        string databaseName;
        CosmosDBAccount? account;

        container = syncAttribute.ContainerName ?? entityName;

        if (syncAttribute.CosmosDbAccountName is null)
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
            account = options.Accounts.FirstOrDefault(x => x.Name.ToLower() == syncAttribute.CosmosDbAccountName.ToLower());
            if (account is null)
                throw new ArgumentException($"Can not find any account by name '{syncAttribute.CosmosDbAccountName}'");
            else
                connectionString = account.ConnectionString;
        }

        if (syncAttribute.CosmosDbDatabaseName is null && account.DefaultDatabaseName is null)
            throw new ArgumentException("No database specified");
        else
            databaseName = syncAttribute.CosmosDbDatabaseName! ?? account.DefaultDatabaseName!;

        return (container, connectionString, databaseName);
    }

    private SyncChangeType ConvertChangeTypeToSyncChangeType(ChangeType changeType)
    {
        if (changeType == ChangeType.Added)
            return SyncChangeType.Added;
        else if(changeType == ChangeType.Deleted)
            return SyncChangeType.Deleted;
        else if (changeType == ChangeType.Modified)
            return SyncChangeType.Modified;
        else
            throw new ArgumentException($"Change type {changeType} is not supported");
    }
}