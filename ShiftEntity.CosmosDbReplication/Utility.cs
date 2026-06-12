using Microsoft.Azure.Cosmos;
using ShiftSoftware.ShiftEntity.CosmosDbReplication.Exceptions;
using ShiftSoftware.ShiftEntity.EFCore.Entities;
using ShiftSoftware.ShiftEntity.Model.Enums;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;

namespace ShiftSoftware.ShiftEntity.CosmosDbReplication;

internal static class Utility
{
    internal static PartitionKey GetPartitionKey(DeletedRowLog row)
    {
        var builder = new PartitionKeyBuilder();

        AddPrtitionKey(builder, row.PartitionKeyLevelOneValue, row.PartitionKeyLevelOneType);
        AddPrtitionKey(builder, row.PartitionKeyLevelTwoValue, row.PartitionKeyLevelTwoType);
        AddPrtitionKey(builder, row.PartitionKeyLevelThreeValue, row.PartitionKeyLevelThreeType);


        return builder.Build();
    }

    internal static PartitionKey GetPartitionKey(ContainerResponse containerResponse, object item)
    {
        PartitionKeyBuilder partitionKeyBuilder = new PartitionKeyBuilder();

        foreach (var partitionKeyPath in containerResponse.Resource.PartitionKeyPaths)
        {
            var path = partitionKeyPath.Substring(1);
            var propertyInfo = item.GetType().GetProperty(path);
            if (propertyInfo is null)
                throw new Exception($"Can not find property for partition key path '{path}'");

            var type = propertyInfo.PropertyType;
            var value = propertyInfo.GetValue(item, null);

            if (!(type == typeof(string) || type.IsNumericType() || type == typeof(bool) || type == typeof(bool?)))
                throw new ArgumentException($"The type or value of '{partitionKeyPath}' partition key is incorrect");

            //A null component must be added as the JSON-null key value. Coercing it instead
            //(Convert.ToString → "", Convert.ToDouble → 0, Convert.ToBoolean → false) builds a key for a DIFFERENT
            //partition than the one the document actually lives in, so point operations (delete/read) silently miss.
            if (value is null)
                partitionKeyBuilder.AddNullValue();
            else if (type == typeof(string))
                partitionKeyBuilder.Add(Convert.ToString(value));
            else if (type.IsNumericType())
                partitionKeyBuilder.Add(Convert.ToDouble(value));
            else
                partitionKeyBuilder.Add(Convert.ToBoolean(value));
        }

        return partitionKeyBuilder.Build();
    }

    internal static void AddPrtitionKey(PartitionKeyBuilder builder, string? value, PartitionKeyTypes type)
    {
        if (type == PartitionKeyTypes.None)
            return;

        //A recorded null component rebuilds as the JSON-null key value, whatever its declared type (see the remark
        //in GetPartitionKey — any other representation addresses the wrong partition).
        if (value is null)
        {
            builder.AddNullValue();
            return;
        }

        if (type == PartitionKeyTypes.String)
            builder.Add(value);
        else if (type == PartitionKeyTypes.Numeric)
            builder.Add(Double.Parse(value, CultureInfo.InvariantCulture));
        else if (type == PartitionKeyTypes.Boolean)
            builder.Add(Boolean.Parse(value));
    }

    /// <summary>
    /// Build the <see cref="LastReplicationStamp"/> (document id + partition-key levels) the given Cosmos item is
    /// being replicated under, for change detection and persistence on the entity.
    /// </summary>
    internal static LastReplicationStamp BuildStamp(ContainerResponse containerResponse, object item)
    {
        var details = GetPartitionKeyDetails(containerResponse, item);

        return new LastReplicationStamp
        {
            Id = Convert.ToString(item.GetProperty("id")),
            Level1 = ToLevel(details.level1),
            Level2 = ToLevel(details.level2),
            Level3 = ToLevel(details.level3),
        };
    }

    private static PartitionKeyLevelStamp? ToLevel((string? value, PartitionKeyTypes type)? level)
        => level.HasValue ? new PartitionKeyLevelStamp { Value = level.Value.value, Type = level.Value.type } : null;

    internal static (PartitionKey? partitionKey,
        (string? value, PartitionKeyTypes type)? level1,
        (string? value, PartitionKeyTypes type)? level2,
        (string? value, PartitionKeyTypes type)? level3) GetPartitionKeyDetails(ContainerResponse containerResponse, object item)
    {
        PartitionKeyBuilder partitionKeyBuilder = new PartitionKeyBuilder();
        List<PropertyInfo?> propertyInfos = new List<PropertyInfo?>();
        List<(string? value, PartitionKeyTypes type)> keys = new();

        foreach (var partitionKeyPath in containerResponse.Resource.PartitionKeyPaths)
        {
            var path = partitionKeyPath.Substring(1);
            var propertyInfo = item.GetType().GetProperty(path);
            if (propertyInfo is null)
                throw new Exception($"Can not find property for partition key path '{path}'");

            var type = propertyInfo.PropertyType;
            var value = propertyInfo.GetValue(item, null);

            if (type == typeof(string))
            {
                //Record null as null, never as "" (Convert.ToString(null) returns ""): the recorded value feeds the
                //LastReplicationStamp and the DeletedRowLog, and a null component written as "" makes every later
                //stale-document delete address the wrong partition and silently miss.
                AddKey(value is null ? null : Convert.ToString(value), PartitionKeyTypes.String);
            }
            else if (type.IsNumericType())
            {
                AddKey(value is null ? null : Convert.ToString(value, CultureInfo.InvariantCulture), PartitionKeyTypes.Numeric);
            }
            else if (type == typeof(bool) || type == typeof(bool?))
            {
                AddKey(value is null ? null : Convert.ToString(value), PartitionKeyTypes.Boolean);
            }
            else
                throw new ArgumentException($"The type or value of '{partitionKeyPath}' partition key is incorrect");
        }

        void AddKey(string? value, PartitionKeyTypes type)
        {
            AddPrtitionKey(partitionKeyBuilder, value, type);
            keys.Add((value, type));
        }

        (string? value, PartitionKeyTypes type)? level1 = keys[0];
        
        (string? value, PartitionKeyTypes type)? level2 = null;
        level2 = keys.Count > 1 ? keys[1] : null;

        (string? value, PartitionKeyTypes type)? level3 = null;
        level3 = keys.Count > 2 ? keys[2] : null;

        return (partitionKeyBuilder.Build(), level1, level2, level3);
    }

    internal static string GetPropertyFullPath<T>(Expression<Func<T, object>> expression)
    {
        var stack = new Stack<string>();
        Expression expr = expression.Body;

        if (expr is UnaryExpression unaryExpression)
        {
            expr = unaryExpression.Operand;
        }

        while (expr is MemberExpression memberExpr)
        {
            stack.Push(memberExpr.Member.Name);
            expr = memberExpr.Expression;
        }

        return string.Join("/", stack);
    }
}
