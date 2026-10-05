using Microsoft.AspNetCore.OData.Query;
using Microsoft.AspNetCore.OData.Edm;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.OData.Edm;
using Microsoft.OData.UriParser;
using ShiftSoftware.ShiftEntity.Core.Pii;
using ShiftSoftware.ShiftEntity.Core.Phones;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.TypeAuth.Core;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ShiftSoftware.ShiftEntity.Web.Pii;

/// <summary>Only direct, positive lookups can address a protected field. Results remain masked.</summary>
internal sealed class PiiODataGuard(IServiceProvider services) : IODataQueryPolicy
{
    public SingleValueNode? Apply<T>(ODataQueryOptions<T> options, SingleValueNode? filter) where T : class
    {
        var model = options.Context.Model;
        var protectedType = new DefaultODataTypeMapper().GetEdmTypeReference(model, typeof(PiiFieldDTO))?.Definition;
        if (protectedType is null && PiiDtoProtector.HasProtectedMembers(typeof(T)) &&
            (filter is not null || options.OrderBy is not null))
            throw Invalid();
        for (var order = options.OrderBy?.OrderByClause; order is not null; order = order.ThenBy)
            if (ContainsProtectedProperty(order.Expression, protectedType)) throw Invalid();

        if (filter is null || !ContainsProtectedProperty(filter, protectedType)) return filter;
        var partial = services.GetRequiredService<ITypeAuthService>()
            .CanAccess(services.GetRequiredService<IOptions<PiiOptions>>().Value.PartialSearchAction);
        return Rewrite(filter);

        SingleValueNode Rewrite(SingleValueNode node)
        {
            if (!ContainsProtectedProperty(node, protectedType)) return node;
            // OData lifts boolean calls to nullable booleans inside compound filters.
            if (node is ConvertNode convert && convert.TypeReference.IsBoolean() && convert.Source.TypeReference.IsBoolean())
                return new ConvertNode(Rewrite(convert.Source), convert.TypeReference);
            if (node is BinaryOperatorNode { OperatorKind: BinaryOperatorKind.And or BinaryOperatorKind.Or } compound)
                return new BinaryOperatorNode(compound.OperatorKind, Rewrite(compound.Left), Rewrite(compound.Right));
            if (node is BinaryOperatorNode { OperatorKind: BinaryOperatorKind.Equal } equal)
            {
                if (TryMember(equal.Left, out var raw, out var declaration) && Literal(equal.Right) is { } value)
                    return new BinaryOperatorNode(BinaryOperatorKind.Equal, raw!, Constant(Normalize(value, declaration!, true)));
                if (TryMember(equal.Right, out raw, out declaration) && Literal(equal.Left) is { } reversed)
                    return new BinaryOperatorNode(BinaryOperatorKind.Equal, raw!, Constant(Normalize(reversed, declaration!, true)));
            }
            if (node is SingleValueFunctionCallNode function && function.Name is "contains" or "startswith" or "endswith")
            {
                var args = function.Parameters.ToArray();
                if (args.Length == 2 && args[0] is SingleValueNode member && TryMember(member, out var raw, out var declaration)
                    && args[1] is SingleValueNode input && Literal(input) is { } value)
                {
                    var literal = Constant(Normalize(value, declaration!, !partial));
                    return partial
                        ? new SingleValueFunctionCallNode(function.Name, new QueryNode[] { raw!, literal }, function.TypeReference)
                        : new BinaryOperatorNode(BinaryOperatorKind.Equal, raw!, literal);
                }
            }
            // Includes negation, null/empty tests, transforms, unresolved aliases, any/all and field comparisons.
            throw Invalid();
        }

        bool TryMember(SingleValueNode node, out SingleValueNode? raw, out PiiAttribute? declaration)
        {
            raw = null;
            declaration = null;
            if (node is not SingleValuePropertyAccessNode leaf ||
                leaf.Source is not SingleComplexNode wrapper || !ReferenceEquals(wrapper.TypeReference.Definition, protectedType) ||
                model.GetClrPropertyName(leaf.Property) is not (nameof(PiiFieldDTO.Value) or nameof(PiiFieldDTO.Display)))
                return false;
            // Root and ordinary nested object paths are supported. Casts and collection variables are not.
            SingleValueNode source = wrapper.Source;
            while (source is SingleComplexNode parent) source = parent.Source;
            if (source is not ResourceRangeVariableReferenceNode range || range.Name != options.Filter!.FilterClause.RangeVariable.Name)
                return false;
            var type = new DefaultODataTypeMapper().GetClrType(model, wrapper.Source.TypeReference);
            var property = type?.GetProperty(model.GetClrPropertyName(wrapper.Property));
            declaration = property is null ? null : PiiFieldProtection.FindDeclaration(property);
            if (declaration?.Revealable != true) throw Invalid();
            var valueProperty = ((IEdmStructuredType)protectedType!).Properties()
                .Single(p => model.GetClrPropertyName(p) == nameof(PiiFieldDTO.Value));
            raw = new SingleValuePropertyAccessNode(wrapper, valueProperty);
            return true;
        }

        string Normalize(string value, PiiAttribute declaration, bool exact)
        {
            if (string.IsNullOrWhiteSpace(value)) throw Invalid();
            if (declaration.Kind != PiiKind.Phone || !exact)
                return value; // Partial phone text matches stored formatting without inventing a country prefix.
            var phones = services.GetService<IPhoneNumberService>();
            if (phones is null)
                throw new ShiftEntityException(new Message("Phone search unavailable", "Configure phone normalization before searching protected phones."), 400);
            if (!phones.TryNormalize(value, out var normalized, out var error))
                throw new ShiftEntityException(new Message("Invalid phone lookup", error), 400);
            return normalized;
        }
    }

    private static string? Literal(SingleValueNode node) => node is ConstantNode { Value: string value } ? value : null;
    private static ConstantNode Constant(string value) => new(value, "'" + value.Replace("'", "''") + "'");
    private static ShiftEntityException Invalid() => new(new Message("Protected field query unavailable",
        "Use a full value or a supported positive text search. Protected ordering and indirect expressions are not supported."), 400);

    private static bool ContainsProtectedProperty(QueryNode root, IEdmType? protectedType)
    {
        var visited = new HashSet<QueryNode>(ReferenceEqualityComparer.Instance);
        return Visit(root);
        bool Visit(QueryNode node)
        {
            if (!visited.Add(node)) return false;
            // Alias definitions are outside this tree. Reject indirect queries on protected DTOs.
            if (node is ParameterAliasNode && protectedType is not null) return true;
            foreach (var property in node.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!property.CanRead || property.GetIndexParameters().Length != 0) continue;
                var value = property.GetValue(node);
                if (value is IEdmProperty edmProperty && protectedType is not null && ReferenceEquals(edmProperty.Type.Definition, protectedType))
                    return true;
                if (value is QueryNode child && Visit(child)) return true;
                if (value is IEnumerable children && value is not string)
                    foreach (var item in children)
                        if (item is QueryNode queryChild && Visit(queryChild)) return true;
            }
            return false;
        }
    }
}
