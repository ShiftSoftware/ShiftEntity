using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.CosmosDbReplication.Exceptions;

internal static class ObjectExtensions
{
    internal static object? GetProperty(this object? obj, string propertyName)
    {
        if (obj == null)
            throw new ArgumentNullException(nameof(obj));

        // Get the type of the object
        Type objectType = obj.GetType();

        // Find the property by name (replace "PropertyName" with your property name)
        PropertyInfo? propertyInfo = objectType.GetProperty(propertyName);

        if (propertyInfo is not null)
            return propertyInfo.GetValue(obj);
        else
            throw new MemberAccessException($"Can not find {propertyName} property in the {objectType.Name}");
    }
}
