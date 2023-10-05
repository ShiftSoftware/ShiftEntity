using System;

namespace ShiftSoftware.ShiftEntity.Model.Replication;

/// <summary>
/// The ItemType should be an Item that contains id property of type string.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public class ShiftEntityReplicationAttribute : Attribute
{
    /// <summary>
    /// There should be an auto-mapper mapping from the entity to this Item, 
    /// and contains id property of type string.
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

/// <summary>
/// It sets the id automatically from the shift entity childes
/// </summary>
/// <typeparam name="Item">There should be an auto-mapper mapping from the entity to this Item, 
/// and contains id property of type string.</typeparam>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public class ShiftEntityReplicationAttribute<Item> : ShiftEntityReplicationAttribute
{
    /// <summary>
    /// There should be an auto-mapper mapping from the entity to this Item, 
    /// and contains id property of type string.
    /// </summary>
    public new Type ItemType { get; private set; }

    public ShiftEntityReplicationAttribute()
    {
        this.ItemType = typeof(Item);
        base.ItemType = typeof(Item);
    }
}