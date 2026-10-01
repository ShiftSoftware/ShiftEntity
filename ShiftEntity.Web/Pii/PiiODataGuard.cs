using Microsoft.AspNetCore.OData.Query;
using Microsoft.AspNetCore.OData.Edm;
using Microsoft.OData.Edm;
using Microsoft.OData.UriParser;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Pii;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Reflection;

namespace ShiftSoftware.ShiftEntity.Web.Pii;

/// <summary>Until classified-field search has a separate policy, reject queries that address protected members.</summary>
internal static class PiiODataGuard
{
    public static void Check<T>(ODataQueryOptions<T> options) where T : class
    {
        var protectedType = new DefaultODataTypeMapper()
            .GetEdmTypeReference(options.Context.Model, typeof(PiiFieldDTO))?.Definition;
        if ((protectedType is null && PiiDtoProtector.HasProtectedMembers(typeof(T)) &&
             (options.Filter is not null || options.OrderBy is not null)) ||
            (options.Filter is not null && ContainsProtectedProperty(options.Filter.FilterClause.Expression, protectedType)) ||
            ContainsProtectedOrder(options.OrderBy?.OrderByClause, protectedType))
        {
            throw new ShiftEntityException(
                new Message("Protected field query unavailable", "Filtering or ordering by a protected field is not supported."),
                (int)HttpStatusCode.BadRequest);
        }
    }

    private static bool ContainsProtectedOrder(OrderByClause? order, IEdmType? protectedType)
    {
        for (var current = order; current is not null; current = current.ThenBy)
            if (ContainsProtectedProperty(current.Expression, protectedType))
                return true;
        return false;
    }

    private static bool ContainsProtectedProperty(QueryNode root, IEdmType? protectedType)
    {
        var visited = new HashSet<QueryNode>(ReferenceEqualityComparer.Instance);
        return Visit(root);

        bool Visit(QueryNode node)
        {
            if (!visited.Add(node))
                return false;
            foreach (var property in node.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!property.CanRead || property.GetIndexParameters().Length != 0)
                    continue;

                var value = property.GetValue(node);
                if (property.Name == "Property" && value is IEdmProperty edmProperty)
                {
                    // Match the wrapper type at this exact property access. A bare
                    // name match can reject an unrelated member elsewhere in the DTO.
                    if (protectedType is not null &&
                        ReferenceEquals(edmProperty.Type.Definition, protectedType))
                        return true;
                }
                if (value is QueryNode child && Visit(child))
                    return true;
                if (value is IEnumerable children && value is not string)
                    foreach (var item in children)
                        if (item is QueryNode queryChild && Visit(queryChild))
                            return true;
            }
            return false;
        }
    }
}
