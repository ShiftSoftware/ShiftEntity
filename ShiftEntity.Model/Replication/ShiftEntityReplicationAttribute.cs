using System;

namespace ShiftSoftware.ShiftEntity.Model.Replication;


[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public class ShiftEntityReplicationAttribute : Attribute
{
    /// <summary>
    /// There should be an auto-mapper mapping from the entity to this type
    /// </summary>
    public Type ItemType { get; set; }

    /// <summary>
    /// If null, gets the entity name
    /// </summary>
    public string? ContainerName { get; set; }

    /// <summary>
    /// If null, gets the default database of the selected account
    /// </summary>
    public string? DatabaseName { get; set; }

    /// <summary>
    /// Name of the account that registerd, if null gets the default account
    /// </summary>
    public string? AccountName { get; set; }
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public class ShiftEntityReplicationAttribute<ItemType> : ShiftEntityReplicationAttribute
{
    public ShiftEntityReplicationAttribute()
    {
        base.ItemType = typeof(ItemType);
    }
}