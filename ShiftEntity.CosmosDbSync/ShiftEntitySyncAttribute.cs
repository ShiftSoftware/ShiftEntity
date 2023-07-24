namespace ShiftSoftware.ShiftEntity.CosmosDbSync;

public class ShiftEntitySyncAttribute : Attribute
{
    public ShiftEntitySyncAttribute(Type cosmosDbItemType, string? collectionName = null, string? cosmosDbDatabaseName = null, string? cosmosDbAccount = null)
    {

    }
}
