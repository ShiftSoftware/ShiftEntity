using System;
using System.Collections.Generic;
using System.Text;

namespace ShiftSoftware.ShiftEntity.Model.Replication;

/// <summary>
/// Update the child references for this parent
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public class PropertyReferenceReplicationAttribute : Attribute
{
    public Type ItemType { get; private set; }
    public string ContainerName { get; private set; }
    public string PropertyName { get; set; }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="itemType">Used to map the entity and then replace it to reference</param>
    /// <param name="containerName"></param>
    /// <param name="propertyName">The reference property name</param>
    public PropertyReferenceReplicationAttribute(Type itemType, string containerName, string propertyName)
    {
        this.ItemType = itemType;
        this.ContainerName = containerName;
        this.PropertyName = propertyName;
    }
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public class PropertyReferenceReplicationAttribute<ItemType> : PropertyReferenceReplicationAttribute
{
    public PropertyReferenceReplicationAttribute(string containerName, string propertyName) : base(typeof(ItemType), containerName, propertyName)
    {
    }
}
