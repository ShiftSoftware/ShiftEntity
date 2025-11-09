using Microsoft.OData;
using Microsoft.OData.Edm;
using Microsoft.OData.UriParser;
using ShiftSoftware.ShiftEntity.Model.HashIds;
using System;
using System.Linq;

namespace ShiftSoftware.ShiftEntity.Web.Services;

public static class OdataHashIdConverter
{
    internal static JsonHashIdConverterAttribute? GetJsonConverterAttribute(string fullTypeName, string propertyName)
    {
        var declaringType = AppDomain.CurrentDomain.GetAssemblies()
                   .Reverse()
                   .Select(assembly => assembly.GetType(fullTypeName))
                   .FirstOrDefault(t => t != null)
               // Safely delete the following part
               // if you do not want fall back to first partial result
               ??
               AppDomain.CurrentDomain.GetAssemblies()
                   .Reverse()
                   .SelectMany(assembly => assembly.GetTypes())
                   .FirstOrDefault(t => t.Name.Contains(fullTypeName));

        if (declaringType == null)
            return null;

        var theProperty = declaringType.GetProperty(propertyName);

        if (theProperty == null)
            return null;

        var jsonHashIdConverterAttribute = (JsonHashIdConverterAttribute)theProperty.GetCustomAttributes(typeof(JsonHashIdConverterAttribute), true).FirstOrDefault()!;

        return jsonHashIdConverterAttribute!;
    }
}

//public class EnableQueryWithHashIdConverter : EnableQueryAttribute
//{
//    public override void OnActionExecuting(ActionExecutingContext actionExecutingContext)
//    {
//        ShiftEntityODataOptions options = actionExecutingContext.HttpContext.RequestServices.GetRequiredService<ShiftEntityODataOptions>();

//        if (!HashId.Enabled)
//        {
//            base.OnActionExecuting(actionExecutingContext);

//            return;
//        }

//        var queryStringValue = actionExecutingContext.HttpContext.Request.QueryString.Value;

//        var originalUrl = actionExecutingContext.HttpContext.Request.GetEncodedUrl();

//        if (!string.IsNullOrWhiteSpace(queryStringValue) && queryStringValue.Contains("$filter") && !actionExecutingContext.HttpContext.Request.Path.Value!.EndsWith("revisions"))
//        {
//            //This will remove the base url all the way to the odata prefix
//            //http://localhost:5028/odata/ToDo?$filter=ID eq 'MQaLZ' will be turned to
//            ///ToDo?$filter=ID eq 'MQaLZ'
//            var relativePath = originalUrl.Substring(originalUrl.IndexOf(options.RoutePrefix) + options.RoutePrefix.Length);

//            ODataUriParser parser = new ODataUriParser(options.EdmModel, new Uri(relativePath, UriKind.Relative));

//            var odataUri = parser.ParseUri();

//            FilterClause filterClause = parser.ParseFilter();

//            if (filterClause == null)
//            {
//                base.OnActionExecuting(actionExecutingContext);

//                return;
//            }

//            var modifiedFilterNode = filterClause.Expression.Accept(new HashIdQueryNodeVisitor());

//            FilterClause modifiedFilterClause = new FilterClause(modifiedFilterNode, filterClause.RangeVariable);

//            odataUri.Filter = modifiedFilterClause;

//            var updatedUrl = odataUri.BuildUri(parser.UrlKeyDelimiter).ToString();

//            var newQueryString = new Microsoft.AspNetCore.Http.QueryString(updatedUrl.Substring(updatedUrl.IndexOf("?")));

//            actionExecutingContext.HttpContext.Request.QueryString = newQueryString;
//        }

//        base.OnActionExecuting(actionExecutingContext);
//    }
//}

public class CollectionConstantVisitor : QueryNodeVisitor<CollectionConstantNode>
{
    public HashIdQueryNodeVisitor HashIdQueryNodeVisitor { get; private set; }

    public CollectionConstantVisitor(HashIdQueryNodeVisitor hashIdQueryNodeVisitor)
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

public class HashIdQueryNodeVisitor : QueryNodeVisitor<SingleValueNode>
{
    public JsonHashIdConverterAttribute? JsonConverterAttribute { get; set; }

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
        var visitor = new HashIdQueryNodeVisitor();

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
            nodeIn.Right.Accept(new CollectionConstantVisitor(this))
        );
    }

    public override SingleValueNode Visit(SingleValuePropertyAccessNode nodeIn)
    {
        if (nodeIn is SingleValuePropertyAccessNode propertyAccess && HasJsonConverterAttributeForProperty(propertyAccess) is JsonHashIdConverterAttribute converterAttribute && converterAttribute != null)
            this.JsonConverterAttribute = converterAttribute;
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
        nodeIn.Body = nodeIn.Body.Accept(this);

        return nodeIn;
    }

    private JsonHashIdConverterAttribute? HasJsonConverterAttributeForProperty(SingleValuePropertyAccessNode propertyAccessNode)
    {
        var fullTypeName = propertyAccessNode.Property.DeclaringType.FullTypeName();
        var propertyName = propertyAccessNode.Property.Name;

        return OdataHashIdConverter.GetJsonConverterAttribute(fullTypeName, propertyName);
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
