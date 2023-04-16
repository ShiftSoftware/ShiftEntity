using Microsoft.AspNetCore.OData.Formatter;
using Microsoft.AspNetCore.OData.Formatter.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OData.Edm;
using Microsoft.OData;
using ShiftSoftware.ShiftEntity.Model.HashId;
using System;
using System.Linq;
using Microsoft.OData.UriParser;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.AspNetCore.Http.Extensions;
using System.Collections.Generic;

namespace ShiftSoftware.ShiftEntity.Web.Services
{
    public static class OdataHashIdConverter
    {
        internal static string RoutePrefix;
        internal static IEdmModel EdmModel;

        public static void RegisterOdataHashIdConverter(this IServiceCollection services, string routePrefix, IEdmModel edmModel)
        {
            RoutePrefix = routePrefix;

            EdmModel = edmModel;

            services.AddSingleton<IODataSerializerProvider>(serviceProvider =>
            {
                return new ODataIDSerializerProvider(serviceProvider);
            });
        }

        internal enum ProcessedHashIdType
        {
            Encoded,
            Decoded
        }

        internal static List<string> GetProcessedHashId(string propertyName, string fullTypeName, List<string> originalValues, ProcessedHashIdType processedHashIdType)
        {
            if (originalValues == null)
                return null;

            if (originalValues.Count == 0)
                return new List<string>();

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

            var hasJsonConverterAttribute = theProperty.CustomAttributes.Any(x =>
                    x.AttributeType == typeof(System.Text.Json.Serialization.JsonConverterAttribute) &&
                    x.ConstructorArguments.Any(y => (y.Value as Type)?.FullName == typeof(JsonHashIdConverter).FullName)
                );

            if (hasJsonConverterAttribute)
            {
                if (processedHashIdType == ProcessedHashIdType.Encoded)
                    return originalValues.Select(x => x == null ? null : HashId.Encode(long.Parse(x))).ToList();
                else if (processedHashIdType == ProcessedHashIdType.Decoded)
                    return originalValues.Select(x => HashId.Decode(x).ToString()).ToList();
            }

            return null;
        }
    }

    public class EnableQueryWithHashIdConverter : EnableQueryAttribute
    {
        public override void OnActionExecuting(ActionExecutingContext actionExecutingContext)
        {
            var queryStringValue = actionExecutingContext.HttpContext.Request.QueryString.Value;

            if (!string.IsNullOrWhiteSpace(queryStringValue))
            {
                var originalUrl = actionExecutingContext.HttpContext.Request.GetEncodedUrl();

                //This will remove the base url all the way to the odata prefix
                //http://localhost:5028/odata/ToDo?$filter=ID eq 'MQaLZ' will be turned to
                ///ToDo?$filter=ID eq 'MQaLZ'
                var relativePath = originalUrl.Substring(originalUrl.IndexOf(OdataHashIdConverter.RoutePrefix) + OdataHashIdConverter.RoutePrefix.Length);

                ODataUriParser parser = new ODataUriParser(OdataHashIdConverter.EdmModel, new Uri(relativePath, UriKind.Relative));

                var odataUri = parser.ParseUri();

                FilterClause filterClause = parser.ParseFilter();

                if (filterClause == null)
                {
                    base.OnActionExecuting(actionExecutingContext);

                    return;
                }

                var modifiedFilterNode = filterClause.Expression.Accept(new HashIdQueryNodeVisitor());

                FilterClause modifiedFilterClause = new FilterClause(modifiedFilterNode, filterClause.RangeVariable);

                odataUri.Filter = modifiedFilterClause;

                var updatedUrl = odataUri.BuildUri(parser.UrlKeyDelimiter).ToString();

                var newQueryString = new Microsoft.AspNetCore.Http.QueryString(updatedUrl.Substring(updatedUrl.IndexOf("?")));

                actionExecutingContext.HttpContext.Request.QueryString = newQueryString;
            }

            base.OnActionExecuting(actionExecutingContext);
        }
    }

    class ODataIDSerializerProvider : ODataSerializerProvider
    {
        public ODataIDSerializerProvider(IServiceProvider serviceProvider) : base(serviceProvider)
        {
        }

        public override IODataEdmTypeSerializer GetEdmTypeSerializer(IEdmTypeReference edmType)
        {
            if (edmType.Definition.TypeKind == EdmTypeKind.Entity)
                return new ODataIDResourceSerializer(this);
            else
                return base.GetEdmTypeSerializer(edmType);
        }
    }

    class ODataIDResourceSerializer : ODataResourceSerializer
    {
        public ODataIDResourceSerializer(ODataSerializerProvider serializerProvider) : base(serializerProvider)
        {
        }

        public override ODataProperty CreateStructuralProperty(IEdmStructuralProperty structuralProperty, ResourceContext resourceContext)
        {
            ODataProperty property = base.CreateStructuralProperty(structuralProperty, resourceContext);

            var decodedValue = OdataHashIdConverter.GetProcessedHashId(
                property.Name, 
                structuralProperty.DeclaringType.FullTypeName(),
                new List<string> { property.Value?.ToString() },
                OdataHashIdConverter.ProcessedHashIdType.Encoded
            )?.FirstOrDefault()?.ToString();

            if (decodedValue != null)
                property.Value = decodedValue;

            return property;
        }
    }

    public class HashIdQueryNodeVisitor : QueryNodeVisitor<SingleValueNode>
    {
        public override SingleValueNode Visit(InNode nodeIn)
        {
            if (nodeIn.Left is SingleValuePropertyAccessNode leftPropertyNode
                && nodeIn.Right is CollectionConstantNode collectionConstant
            )
            {
                var property = leftPropertyNode.Property;

                var fullTypeName = property.DeclaringType.FullTypeName();

                var values = collectionConstant.Collection.Select(x => x.Value.ToString()).ToList();

                var decodedValues = OdataHashIdConverter.GetProcessedHashId(property.Name, fullTypeName, values, OdataHashIdConverter.ProcessedHashIdType.Decoded);

                if (decodedValues == null)
                    return nodeIn;

                var newCollection = new CollectionConstantNode(decodedValues.Select(x => x), $"({string.Join(",", decodedValues.Select(x => $"'{x}'"))})", collectionConstant.CollectionType);

                return new InNode(nodeIn.Left, newCollection);
            }

            return nodeIn;
        }

        public override SingleValueNode Visit(BinaryOperatorNode nodeIn)
        {
            SingleValueNode visitedLeft = nodeIn.Left;
            SingleValueNode visitedRight = nodeIn.Right;

            if (nodeIn.Left is BinaryOperatorNode left)
            {
                visitedLeft = Visit(left);
            }

            if (nodeIn.Right is BinaryOperatorNode right)
            {
                visitedRight = Visit(right);
            }

            ConstantNode theConstantNode = null;
            SingleValuePropertyAccessNode thePropertyAccessNode = null;

            if (nodeIn.Left is ConstantNode)
            {
                theConstantNode = nodeIn.Left as ConstantNode;
            }

            if (nodeIn.Right is ConstantNode)
            {
                theConstantNode = nodeIn.Right as ConstantNode;
            }

            if (nodeIn.Left is ConvertNode leftConvertNode)
            {
                if (leftConvertNode.Source is SingleValuePropertyAccessNode leftPropertyAccessNode)
                    thePropertyAccessNode = leftPropertyAccessNode;

                if (leftConvertNode.Source is InNode lefInNode)
                    visitedLeft = Visit(lefInNode);
            }

            if (nodeIn.Right is ConvertNode rightConvertNode)
            {
                if (rightConvertNode.Source is SingleValuePropertyAccessNode rightPropertyAccessNode)
                    thePropertyAccessNode = rightPropertyAccessNode;

                if (rightConvertNode.Source is InNode rightInNode)
                    visitedRight = Visit(rightInNode);
            }

            if (nodeIn.Left is SingleValuePropertyAccessNode leftSingleValuePropertyAccessNode)
            {
                thePropertyAccessNode = leftSingleValuePropertyAccessNode;
            }

            if (nodeIn.Right is SingleValuePropertyAccessNode rightSingleValuePropertyAccessNode)
            {
                thePropertyAccessNode = rightSingleValuePropertyAccessNode;
            }

            if (theConstantNode != null && thePropertyAccessNode != null)
            {
                var property = thePropertyAccessNode.Property;

                var decodedValue = OdataHashIdConverter.GetProcessedHashId(
                    property.Name,
                    property.DeclaringType.FullTypeName(),
                    new List<string> { theConstantNode.Value?.ToString() },
                    OdataHashIdConverter.ProcessedHashIdType.Decoded
                )?.FirstOrDefault()?.ToString();

                if (decodedValue == null)
                    return nodeIn;

                var updatedConstantNode = new ConstantNode("", $"'{decodedValue}'");

                if (!HashId.acceptUnencodedIds && !(nodeIn.OperatorKind == BinaryOperatorKind.Equal || nodeIn.OperatorKind == BinaryOperatorKind.NotEqual))
                {
                    throw new ODataException("HashId values only accepts Equals & Not Equals operator");
                }

                return new BinaryOperatorNode(nodeIn.OperatorKind, thePropertyAccessNode, updatedConstantNode);
            }

            return new BinaryOperatorNode(nodeIn.OperatorKind, visitedLeft, visitedRight);
            //return nodeIn;
        }

        public override SingleValueNode Visit(SingleValueFunctionCallNode nodeIn)
        {
            SingleValuePropertyAccessNode thePropertyAccessNode = null;
            ConstantNode theConstantNode = null;

            theConstantNode = nodeIn.Parameters.Where(x => x is ConstantNode).Select(x => x as ConstantNode).FirstOrDefault();

            SingleValueFunctionCallNode nodeToCheckProperty = nodeIn.Parameters.Where(x => x is SingleValueFunctionCallNode).Select(x => x as SingleValueFunctionCallNode).FirstOrDefault();

            if (nodeToCheckProperty == null)
                nodeToCheckProperty = nodeIn;

            thePropertyAccessNode = nodeToCheckProperty.Parameters.Where(x => x is SingleValuePropertyAccessNode).Select(x => x as SingleValuePropertyAccessNode).FirstOrDefault();

            if (thePropertyAccessNode == null)
                thePropertyAccessNode = nodeToCheckProperty.Parameters.Where(x => x is ConvertNode convertNode && convertNode.Source is SingleValuePropertyAccessNode).Select(x => (x as ConvertNode).Source as SingleValuePropertyAccessNode).FirstOrDefault();

            if (theConstantNode != null && thePropertyAccessNode != null)
            {
                var property = thePropertyAccessNode.Property;

                var decodedValue = OdataHashIdConverter.GetProcessedHashId(
                    property.Name,
                    property.DeclaringType.FullTypeName(),
                    new List<string> { theConstantNode.Value?.ToString() },
                    OdataHashIdConverter.ProcessedHashIdType.Decoded
                )?.FirstOrDefault()?.ToString();

                if (decodedValue == null)
                    return nodeIn;

                var updatedConstantNode = new ConstantNode("", $"'{decodedValue}'");

                var newFunction = new SingleValueFunctionCallNode(nodeIn.Name, nodeIn.Functions, new List<QueryNode>
                {
                    nodeIn == nodeToCheckProperty ?
                    thePropertyAccessNode :
                    new SingleValueFunctionCallNode(nodeToCheckProperty.Name, nodeToCheckProperty.Functions, new List<QueryNode> { 
                        thePropertyAccessNode,
                    }, nodeToCheckProperty.TypeReference, nodeToCheckProperty.Source),
                    updatedConstantNode
                }, nodeIn.TypeReference, nodeIn.Source);

                return newFunction;
            }

            return nodeIn;
        }
    }
}
