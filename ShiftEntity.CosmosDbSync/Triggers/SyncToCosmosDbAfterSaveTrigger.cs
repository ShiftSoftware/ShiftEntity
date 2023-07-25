using EntityFrameworkCore.Triggered;
using Microsoft.EntityFrameworkCore.Metadata.Internal;
using Microsoft.EntityFrameworkCore;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.CosmosDbSync.Services;

namespace ShiftSoftware.ShiftEntity.CosmosDbSync.Triggers;


internal class SyncToCosmosDbAfterSaveTrigger<EntityType> : IAfterSaveTrigger<EntityType>
    where EntityType : ShiftEntity<EntityType>
{
    private readonly ShiftEntityCosmosDbOptions options;
    private readonly CosmosDBService<EntityType> cosmosDBService;

    public SyncToCosmosDbAfterSaveTrigger(ShiftEntityCosmosDbOptions options,CosmosDBService<EntityType> cosmosDBService)
    {
        this.options = options;
        this.cosmosDBService = cosmosDBService;
    }

    public Task AfterSave(ITriggerContext<EntityType> context, CancellationToken cancellationToken)
    {
        var entityType = context.Entity.GetType();

        var syncAttribute = (ShiftEntitySyncAttribute)entityType.GetCustomAttributes(true).LastOrDefault(x => x as ShiftEntitySyncAttribute != null);

        if (syncAttribute != null)
        {
            var configurations = GetConfigurations(syncAttribute, entityType.Name);

            if (context.ChangeType == ChangeType.Modified || context.ChangeType == ChangeType.Modified)
                cosmosDBService.UpsertAsync(context.Entity, syncAttribute.CosmosDbItemType, 
                    configurations.collection, configurations.databaseName, configurations.connectionString);
            else if(context.ChangeType == ChangeType.Deleted)
                cosmosDBService.DeleteAsync(context.Entity, syncAttribute.CosmosDbItemType,
                    configurations.collection, configurations.databaseName, configurations.connectionString);
        }

        return Task.CompletedTask;
    }

    private (string collection, string connectionString, string databaseName) GetConfigurations
        (ShiftEntitySyncAttribute syncAttribute, string entityName)
    {
        string collection;
        string connectionString;
        string databaseName;
        CosmosDBAccount? account;

        collection = syncAttribute.CollectionName ?? entityName;

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

        return (collection, connectionString, databaseName);
    }
}