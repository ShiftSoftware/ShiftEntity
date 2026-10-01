using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using ShiftSoftware.ShiftEntity.Model.Dtos;

namespace ShiftSoftware.ShiftEntity.Core.Pii;

/// <summary>Clears raw wrapper values after mapping and before an ordinary response leaves the server.</summary>
public sealed class PiiDtoProtector(PiiFieldProtection fields)
{
    private readonly ConditionalWeakTable<PiiFieldDTO, ProtectedSnapshot> protectedFields = new();

    private sealed record ProtectedSnapshot(string? Display);

    public static bool HasProtectedMembers(Type type) => HasProtectedMembers(type, new HashSet<Type>());

    public static ISet<string> GetProtectedMemberNames(Type type)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectProtectedMemberNames(type, new HashSet<Type>(), names);
        return names;
    }

    private static void CollectProtectedMemberNames(Type type, HashSet<Type> visited, ISet<string> names)
    {
        if (IsScalar(type) || !visited.Add(type))
            return;
        if (type.IsArray)
        {
            CollectProtectedMemberNames(type.GetElementType()!, visited, names);
            return;
        }
        if (type.IsGenericType && typeof(IEnumerable).IsAssignableFrom(type))
            foreach (var argument in type.GetGenericArguments())
                CollectProtectedMemberNames(argument, visited, names);
        if (type.Namespace?.StartsWith("System", StringComparison.Ordinal) == true)
            return;
        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.PropertyType == typeof(PiiFieldDTO))
                names.Add(property.Name);
            else
                CollectProtectedMemberNames(property.PropertyType, visited, names);
        }
    }

    private static bool HasProtectedMembers(Type type, HashSet<Type> visited)
    {
        if (type == typeof(PiiFieldDTO))
            return true;
        if (IsScalar(type) || !visited.Add(type))
            return false;
        if (type.IsArray)
            return HasProtectedMembers(type.GetElementType()!, visited);
        if (type.IsGenericType && typeof(IEnumerable).IsAssignableFrom(type))
        {
            foreach (var argument in type.GetGenericArguments())
                if (HasProtectedMembers(argument, visited))
                    return true;
        }
        if (type.Namespace?.StartsWith("System", StringComparison.Ordinal) == true)
            return false;
        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (HasProtectedMembers(property.PropertyType, visited))
                return true;
        }
        return false;
    }

    public void Protect(object? dto)
    {
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        Visit(dto, visited);
    }

    private void Visit(object? value, HashSet<object> visited)
    {
        if (value is null)
            return;

        var type = value.GetType();
        if (value is PiiFieldDTO || (type.IsValueType && HasProtectedMembers(type)))
            throw new InvalidOperationException("Protected fields need a writable class member with a PII declaration.");
        if (IsScalar(type) || !visited.Add(value))
            return;

        if (value is IEnumerable items)
        {
            foreach (var item in items)
                Visit(item, visited);
            return;
        }

        if (type.Namespace?.StartsWith("System", StringComparison.Ordinal) == true)
            return;

        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!property.CanRead || property.GetIndexParameters().Length != 0)
                continue;

            if (property.PropertyType == typeof(PiiFieldDTO))
            {
                if (!property.CanWrite)
                    throw new InvalidOperationException($"Protected DTO member {type.Name}.{property.Name} must be writable.");

                var wrapper = (PiiFieldDTO?)property.GetValue(value);
                if (wrapper is not null && wrapper.Value is null && wrapper.Write == "keep" &&
                    protectedFields.TryGetValue(wrapper, out var snapshot) && snapshot.Display == wrapper.Display)
                    continue;

                var protectedField = fields.Protect(type, property.Name, wrapper?.Value);
                protectedFields.Add(protectedField, new ProtectedSnapshot(protectedField.Display));
                property.SetValue(value, protectedField);
            }
            else
            {
                Visit(property.GetValue(value), visited);
            }
        }
    }

    private static bool IsScalar(Type type)
        => type == typeof(string) || type.IsPrimitive || type.IsEnum || type == typeof(decimal) ||
           type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(Guid) ||
           type == typeof(TimeSpan);
}
