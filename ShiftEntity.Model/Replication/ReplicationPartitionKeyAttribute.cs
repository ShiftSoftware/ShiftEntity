using System;

namespace ShiftSoftware.ShiftEntity.Model.Replication;

/// <summary>
/// The property name must be the same name of the partition key of cosmos db container, 
/// and the name is case sensitive, the property type must be either boolean or string or number.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public class ReplicationPartitionKeyAttribute : Attribute
{
    public string PropertyName { get; private set; }

    public ReplicationPartitionKeyAttribute(string propertyName)
    {
        PropertyName = propertyName;
    }
}
