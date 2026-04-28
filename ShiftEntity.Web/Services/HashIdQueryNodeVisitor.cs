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
    private readonly HashIdQueryNodeVisitor<T> _hashIdQueryNodeVisitor;

    public CollectionConstantVisitor(HashIdQueryNodeVisitor<T> hashIdQueryNodeVisitor)
    {
        _hashIdQueryNodeVisitor = hashIdQueryNodeVisitor ?? throw new ArgumentNullException(nameof(hashIdQueryNodeVisitor));
    }

    public override CollectionConstantNode Visit(CollectionConstantNode nodeIn)
    {
        // Preserve the current converter state before visiting collection items
        var converterBeforeVisit = _hashIdQueryNodeVisitor.JsonConverterAttribute;
        
        var newCollection = nodeIn.Collection.Select(x =>
        {
            // Restore the converter for each item in the collection
            _hashIdQueryNodeVisitor.JsonConverterAttribute = converterBeforeVisit;
            return x.Accept(_hashIdQueryNodeVisitor);
        }).ToList();

        // Build the literal string representation without quotes for numeric values
        var literalText = "(" + string.Join(",", newCollection.Select(node =>
        {
            if (node is ConstantNode constantNode && constantNode.Value != null)
            {
                // If it's already a number (decoded HashId), don't add quotes
                if (constantNode.Value is long || constantNode.Value is int)
                    return constantNode.Value.ToString();
                
                // For strings, add quotes if not already present
                var valueStr = constantNode.Value.ToString()!;
                return valueStr.StartsWith("'") ? valueStr : $"'{valueStr}'";
            }
            return "null";
        })) + ")";

        return new CollectionConstantNode(newCollection, literalText, nodeIn.CollectionType);
    }
}

public class HashIdQueryNodeVisitor<T> : QueryNodeVisitor<SingleValueNode>
{
    private readonly Type _rootType;
    private JsonHashIdConverterAttribute? _preservedConverterAttribute;

    // Cache for property converter attributes to avoid repeated reflection
    internal static readonly ConcurrentDictionary<(Type, string), JsonHashIdConverterAttribute?> _converterCache = new();

    public JsonHashIdConverterAttribute? JsonConverterAttribute { get; set; }

    public HashIdQueryNodeVisitor()
    {
        _rootType = typeof(T);
    }

    /// <summary>
    /// Gets the JsonHashIdConverterAttribute for a property on the specified type.
    /// </summary>
    internal static JsonHashIdConverterAttribute? GetJsonConverterAttribute(Type type, string propertyName)
    {
        if (type == null || string.IsNullOrWhiteSpace(propertyName))
            return null;

        return _converterCache.GetOrAdd((type, propertyName), key =>
        {
            var (declType, propName) = key;
            var property = declType.GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);

            return property?.GetCustomAttribute<JsonHashIdConverterAttribute>(inherit: true);
        });
    }

    /// <summary>
    /// Clears the internal cache. Useful for testing scenarios.
    /// </summary>
    internal static void ClearCache()
    {
        _converterCache.Clear();
    }

    public override SingleValueNode Visit(ConstantNode nodeIn)
    {
        if (JsonConverterAttribute is null)
            return nodeIn;

        try
        {
            // Clear the attribute so it doesn't affect subsequent nodes
            var converter = JsonConverterAttribute;
            JsonConverterAttribute = null;

            // Decode the hash ID to get the actual numeric value
            var hashIdString = nodeIn.Value?.ToString();
            if (string.IsNullOrWhiteSpace(hashIdString))
                return nodeIn;

            var decodedValue = converter.Hashids!.Decode(hashIdString);

            // Return a new ConstantNode with the decoded long value
            return new ConstantNode(decodedValue.ToString(), $"'{decodedValue}'");
        }
        catch (Exception ex)
        {
            // If decoding fails, reset the attribute and return original node
            JsonConverterAttribute = null;
            
            // Optionally log the error or throw a more specific exception
            throw new ODataException($"Failed to decode HashId value '{nodeIn.Value}': {ex.Message}", ex);
        }
    }

    public override SingleValueNode Visit(BinaryOperatorNode nodeIn)
    {
        // Create a child visitor that inherits the current state
        var visitor = new HashIdQueryNodeVisitor<T>
        {
            JsonConverterAttribute = JsonConverterAttribute,
            _preservedConverterAttribute = _preservedConverterAttribute
        };

        // Visit left and right operands
        var visitedLeft = nodeIn.Left is BinaryOperatorNode leftBinary
            ? Visit(leftBinary)
            : nodeIn.Left.Accept(visitor);

        var visitedRight = nodeIn.Right is BinaryOperatorNode rightBinary
            ? Visit(rightBinary)
            : nodeIn.Right.Accept(visitor);

        // Validate operator compatibility with HashIds
        if (visitor.JsonConverterAttribute is not null && 
            !HashId.acceptUnencodedIds && 
            nodeIn.OperatorKind != BinaryOperatorKind.Equal && 
            nodeIn.OperatorKind != BinaryOperatorKind.NotEqual)
        {
            throw new ODataException(
                $"HashId values only support 'eq' and 'ne' operators when unencoded IDs are not allowed. " +
                $"Operator '{nodeIn.OperatorKind}' is not supported.");
        }

        return new BinaryOperatorNode(nodeIn.OperatorKind, visitedLeft, visitedRight);
    }

    public override SingleValueNode Visit(ConvertNode nodeIn)
    {
        // Pass through conversion nodes, preserving the converter state
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
        if (nodeIn?.Property == null)
            return nodeIn!;

        // Check if this specific property has a converter
        var converterForThisProperty = GetJsonConverterAttribute(_rootType, nodeIn.Property.Name);

        if (converterForThisProperty != null)
        {
            // This property has its own converter, use it
            JsonConverterAttribute = converterForThisProperty;
        }
        else if (_preservedConverterAttribute != null)
        {
            // No specific converter, but we have one from parent collection (e.g., from Any node)
            // Keep using the preserved one
            JsonConverterAttribute = _preservedConverterAttribute;
        }
        else
        {
            // No converter found
            JsonConverterAttribute = null;
        }

        return nodeIn;
    }

    public override SingleValueNode Visit(SingleValueFunctionCallNode nodeIn)
    {
        // Visit all parameters with the current converter state
        var visitedParameters = nodeIn.Parameters.Select(x => x.Accept(this));
        
        return new SingleValueFunctionCallNode(
            nodeIn.Name,
            visitedParameters,
            nodeIn.TypeReference
        );
    }

    public override SingleValueNode Visit(AnyNode nodeIn)
    {
        JsonHashIdConverterAttribute? converter = null;

        try
        {
            var source = UnwrapConvertNodes(nodeIn.Source);

            if (source != null)
            {
                // Extract property from the source node
                var property = ExtractPropertyFromNode(source);
                
                if (property != null)
                {
                    converter = GetJsonConverterAttribute(_rootType, property.Name);
                }
            }
        }
        catch
        {
            // Silently ignore errors and proceed without converter
        }

        // Create a child visitor with the converter preserved throughout the Any body
        var childVisitor = new HashIdQueryNodeVisitor<T>
        {
            JsonConverterAttribute = converter,
            _preservedConverterAttribute = converter
        };

        nodeIn.Body = nodeIn.Body.Accept(childVisitor);

        return nodeIn;
    }

    public override SingleValueNode Visit(UnaryOperatorNode nodeIn)
    {
        var visitedOperand = nodeIn.Operand.Accept(this);
        return new UnaryOperatorNode(nodeIn.OperatorKind, visitedOperand);
    }

    public override SingleValueNode Visit(NonResourceRangeVariableReferenceNode nodeIn)
    {
        // Range variables (like 'item' in any/all) don't need conversion themselves
        // The converter is already set by the parent Any/All node
        return nodeIn;
    }

    /// <summary>
    /// Unwraps ConvertNode wrappers to get to the underlying node.
    /// </summary>
    private static QueryNode? UnwrapConvertNodes(QueryNode? node)
    {
        while (node is ConvertNode convertNode)
        {
            node = convertNode.Source;
        }
        return node;
    }

    /// <summary>
    /// Extracts the IEdmProperty from various node types using reflection.
    /// </summary>
    private static IEdmProperty? ExtractPropertyFromNode(QueryNode node)
    {
        // Try to get the Property using reflection (works for CollectionPropertyAccessNode, etc.)
        var propertyInfo = node.GetType().GetProperty("Property");
        return propertyInfo?.GetValue(node) as IEdmProperty;
    }
}
