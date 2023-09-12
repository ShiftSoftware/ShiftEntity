
namespace ShiftSoftware.ShiftEntity.CosmosDbReplication;


[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public class ShiftEntityReplicationAttribute : Attribute
{
    /// <summary>
    /// There should be an auto-mapper mapping from the entity to this type
    /// </summary>
    public Type CosmosDbItemType { get; set; }

    /// <summary>
    /// If null, gets the entity name
    /// </summary>
    public string? ContainerName { get; set; }

    /// <summary>
    /// If null, gets the default database of the selected account
    /// </summary>
    public string? CosmosDbDatabaseName { get; set; }

    /// <summary>
    /// Name of the account that registerd, if null gets the default account
    /// </summary>
    public string? CosmosDbAccountName { get; set; }
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public class ShiftEntityReplicationAttribute<ItemType> : ShiftEntityReplicationAttribute
{
    public ShiftEntityReplicationAttribute()
    {
        CosmosDbItemType = typeof(ItemType);
    }
}