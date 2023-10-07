using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Model.Replication;

/// <summary>
/// Update the reference item in the CosmosDb after save the entity
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public class ReferenceReplicationAttribute : Attribute
{
    public Type ItemType { get; private set; }
    public IEnumerable<string> ComparePropertyNames { get; private set; }
    public string ContainerName { get; private set; }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="itemType">Used to map the entity and then replace it to container,
    /// and should ignore id and partition keys during mapping, if not used as compare parameter</param>
    /// <param name="compairPropertyName">Property name to compare to find right reference,
    /// must be exists in ItemType object</param>
    public ReferenceReplicationAttribute(Type itemType, string containerName, params string[] comparePropertyName)
    {
        this.ItemType = itemType;
        this.ComparePropertyNames = comparePropertyName;
        this.ContainerName = containerName;
    }
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public class ReferenceReplicationAttribute<ItemType> : ReferenceReplicationAttribute
{
    public ReferenceReplicationAttribute(string containerName, params string[] comparePropertyName) : base(typeof(ItemType),containerName, comparePropertyName)
    {
    }
}
