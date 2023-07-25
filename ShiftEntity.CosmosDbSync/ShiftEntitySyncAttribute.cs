namespace ShiftSoftware.ShiftEntity.CosmosDbSync;

[AttributeUsage(AttributeTargets.Class, AllowMultiple =false)]
public class ShiftEntitySyncAttribute : Attribute
{
    public Type CosmosDbItemType { get; }
    public string? CollectionName { get; }
    public string? CosmosDbDatabaseName { get; }
    public string? CosmosDbAccountName { get; }

    public ShiftEntitySyncAttribute(Type cosmosDbItemType, 
        string? collectionName = null, 
        string? cosmosDbDatabaseName = null, 
        string? cosmosDbAccountName = null)
    {
        CosmosDbItemType = cosmosDbItemType;
        CollectionName = collectionName;
        CosmosDbDatabaseName = cosmosDbDatabaseName;
        CosmosDbAccountName = cosmosDbAccountName;
    }
}