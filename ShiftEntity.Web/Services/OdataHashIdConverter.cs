using Microsoft.OData;
using Microsoft.OData.Edm;
using Microsoft.OData.UriParser;
using ShiftSoftware.ShiftEntity.Model.HashIds;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;

namespace ShiftSoftware.ShiftEntity.Web.Services;

public class CollectionConstantVisitor<T> : QueryNodeVisitor<CollectionConstantNode>
{
    public HashIdQueryNodeVisitor<T> HashIdQueryNodeVisitor { get; private set; }

    public CollectionConstantVisitor(HashIdQueryNodeVisitor<T> hashIdQueryNodeVisitor)
    {
        this.HashIdQueryNodeVisitor = hashIdQueryNodeVisitor;
    }

    public override CollectionConstantNode Visit(CollectionConstantNode nodeIn)
    {
        var newCollection = nodeIn.Collection.Select(x => x.Accept(this.HashIdQueryNodeVisitor));

        return new CollectionConstantNode(
            newCollection,
            "(" + string.Join(",", newCollection.Select(y =>
            {
                var constantNode = (y as ConstantNode)!;

                var value = constantNode!.Value!;

                if (!value!.ToString()!.StartsWith("'"))
                    value = $"'{value}'";

                return value;
            })) + ")",
            nodeIn.CollectionType
        );
    }
}

public class HashIdQueryNodeVisitor<T> : QueryNodeVisitor<SingleValueNode>
{
    private readonly Type _rootType;

    private JsonHashIdConverterAttribute? _preservedConverterAttribute;

    // Cache for property converter attributes to avoid repeated reflection
    internal static readonly ConcurrentDictionary<(Type, string), JsonHashIdConverterAttribute?> _converterCache = new();

    /// <summary>
    /// Gets the JsonHashIdConverterAttribute for a property on the specified type.
    /// </summary>
    internal static JsonHashIdConverterAttribute? GetJsonConverterAttribute(Type type, string propertyName)
    {
        return _converterCache.GetOrAdd((type, propertyName), key =>
        {
            var (declType, propName) = key;
            var property = declType.GetProperty(propName);

            if (property == null)
                return null;

            return property.GetCustomAttribute<JsonHashIdConverterAttribute>(inherit: true);
        });
    }

    public JsonHashIdConverterAttribute? JsonConverterAttribute { get; set; }

    public HashIdQueryNodeVisitor()
    {
        _rootType = typeof(T);
    }

    public override SingleValueNode Visit(ConstantNode nodeIn)
    {
        if (JsonConverterAttribute is JsonHashIdConverterAttribute converterAttribute && converterAttribute != null)
        {
            this.JsonConverterAttribute = null;

            return new ConstantNode(
                $"'{converterAttribute!.Hashids!.Decode(nodeIn.Value.ToString()!)}'",
                $"'{converterAttribute.Hashids.Decode(nodeIn.Value.ToString()!)}'"
            );
        }

        return nodeIn;
    }

    public override SingleValueNode Visit(BinaryOperatorNode nodeIn)
    {
        var visitor = new HashIdQueryNodeVisitor<T>()
        {
            JsonConverterAttribute = this.JsonConverterAttribute,
            _preservedConverterAttribute = this._preservedConverterAttribute
        };

        var visitedLeft = nodeIn.Left is BinaryOperatorNode leftBinary ?
            Visit(leftBinary) :
            nodeIn.Left.Accept(visitor);

        var visitedRight = nodeIn.Right is BinaryOperatorNode rightBinary ?
            Visit(rightBinary) :
            nodeIn.Right.Accept(visitor);

        if (visitor.JsonConverterAttribute is not null && !HashId.acceptUnencodedIds && !(nodeIn.OperatorKind == BinaryOperatorKind.Equal || nodeIn.OperatorKind == BinaryOperatorKind.NotEqual))
            throw new ODataException("HashId values only accept Equals & Not Equals operators when the JsonConverterAttribute is present and unencoded IDs are not allowed.");

        return new BinaryOperatorNode(
            nodeIn.OperatorKind,
            visitedLeft,
            visitedRight
        );
    }

    public override SingleValueNode Visit(ConvertNode nodeIn)
    {
        return nodeIn.Source.Accept(this);
    }

    public override SingleValueNode Visit(InNode nodeIn)
    {
        return new InNode(
            nodeIn.Left.Accept(this),
            nodeIn.Right.Accept(new CollectionConstantVisitor<T>(this))
        );
    }

    public override SingleValueNode Visit(SingleValuePropertyAccessNode nodeIn)
    {
        // Check if we have a preserved converter from an Any node
        // If so, don't overwrite it unless we find a more specific one on this property
        var converterForThisProperty = GetJsonConverterAttribute(_rootType, nodeIn.Property.Name);

        if (converterForThisProperty != null)
        {
            // This property has its own converter, use it
            this.JsonConverterAttribute = converterForThisProperty;
        }
        else if (_preservedConverterAttribute != null)
        {
            // This property doesn't have a converter, but we have one from the parent collection
            // Keep using the preserved one (don't reset to null)
            this.JsonConverterAttribute = _preservedConverterAttribute;
        }
        else
            this.JsonConverterAttribute = null;

        return nodeIn;
    }

    public override SingleValueNode Visit(SingleValueFunctionCallNode nodeIn)
    {
        return new SingleValueFunctionCallNode(
            nodeIn.Name,
            nodeIn.Parameters.Select(x => x.Accept(this)),
            nodeIn.TypeReference
        );
    }

    public override SingleValueNode Visit(AnyNode nodeIn)
    {
        // When we encounter an Any node, infer the converter from the Source collection property.
        // Example: DepartmentIds/any(item: item eq 'bPp2M')
        // Example: Departments/any(item: item/Value eq 'bPp2M')
        // The Source is the collection property which carries the DepartmentHashIdConverter attribute.
        JsonHashIdConverterAttribute? converter = null;

        try
        {
            QueryNode? source = nodeIn.Source;

            // Unwrap ConvertNode wrappers
            while (source is ConvertNode convertSource)
                source = convertSource.Source;

            // Try to extract property info from various node types
            if (source != null)
            {
                // Use reflection to get Property from CollectionPropertyAccessNode or similar
                var propertyInfo = source.GetType().GetProperty("Property");
                if (propertyInfo != null)
                {
                    var property = propertyInfo.GetValue(source) as IEdmProperty;
                    if (property != null)
                    {
                        var propertyName = property.Name;
                        // Use the root type T to look up the property
                        converter = GetJsonConverterAttribute(_rootType, propertyName);
                    }
                }
            }
        }
        catch
        {
            // Ignore errors and fall back to default behavior
        }

        // Create a child visitor with the converter set for the body traversal
        // Use _preservedConverterAttribute so nested property access (like item/Value) doesn't lose the converter
        var childVisitor = new HashIdQueryNodeVisitor<T>()
        {
            JsonConverterAttribute = converter,
            _preservedConverterAttribute = converter
        };
        nodeIn.Body = nodeIn.Body.Accept(childVisitor);

        return nodeIn;
    }

    public override SingleValueNode Visit(UnaryOperatorNode nodeIn)
    {
        return new UnaryOperatorNode(nodeIn.OperatorKind, nodeIn.Operand.Accept(this));
    }

    public override SingleValueNode Visit(NonResourceRangeVariableReferenceNode nodeIn)
    {
        return nodeIn;
    }
}
