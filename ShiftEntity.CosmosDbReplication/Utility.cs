using Microsoft.Azure.Cosmos;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.CosmosDbReplication.Exceptions;
using ShiftSoftware.ShiftEntity.EFCore.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

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

            if (type == typeof(string))
                partitionKeyBuilder.Add(Convert.ToString(value));
            else if (type.IsNumericType())
                partitionKeyBuilder.Add(Convert.ToDouble(value));
            else if (type == typeof(bool) || type == typeof(bool?))
                partitionKeyBuilder.Add(Convert.ToBoolean(value));
            else
                throw new ArgumentException($"The type or value of '{partitionKeyPath}' partition key is incorrect");
        }

        return partitionKeyBuilder.Build();
    }

    internal static void AddPrtitionKey(PartitionKeyBuilder builder, string? value, PartitionKeyTypes type)
    {
        if (type == PartitionKeyTypes.String)
            builder.Add(value);
        else if (type == PartitionKeyTypes.Numeric)
            builder.Add(Double.Parse(value));
        else if (type == PartitionKeyTypes.Boolean)
            builder.Add(Boolean.Parse(value));
    }

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
                partitionKeyBuilder.Add(Convert.ToString(value));
                keys.Add((Convert.ToString(value), PartitionKeyTypes.String));
            }
            else if (type.IsNumericType())
            {
                partitionKeyBuilder.Add(Convert.ToDouble(value));
                keys.Add((Convert.ToString(value), PartitionKeyTypes.Numeric));
            }
            else if (type == typeof(bool) || type == typeof(bool?))
            {
                partitionKeyBuilder.Add(Convert.ToBoolean(value));
                keys.Add((Convert.ToString(value), PartitionKeyTypes.Boolean));
            }
            else
                throw new ArgumentException($"The type or value of '{partitionKeyPath}' partition key is incorrect");
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
