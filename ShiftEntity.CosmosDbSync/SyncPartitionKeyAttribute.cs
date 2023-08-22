using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.CosmosDbSync;

/// <summary>
/// The property name must be the same name of the partition key of cosmos db container, 
/// and the name is case sensitive, the property type must be either boolean or string or number.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public class SyncPartitionKeyAttribute: Attribute
{
    public string PropertyName { get; private set; }

    public SyncPartitionKeyAttribute(string propertyName)
    {
        this.PropertyName = propertyName;
    }
}
