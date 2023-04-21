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

        internal static JsonHashIdConverterAttribute GetJsonConverterAttribute(string fullTypeName, string propertyName)
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

            var jsonHashIdConverterAttribute = (JsonHashIdConverterAttribute)theProperty.GetCustomAttributes(typeof(JsonHashIdConverterAttribute), true).FirstOrDefault();

            return jsonHashIdConverterAttribute;
        }
    }

    public class EnableQueryWithHashIdConverter : EnableQueryAttribute
    {
        public override void OnActionExecuting(ActionExecutingContext actionExecutingContext)
        {
            if (!HashId.Enabled)
            {
                base.OnActionExecuting(actionExecutingContext);

                return;
            }

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

            if (!HashId.Enabled)
                return property;

            if (property.Value != null && OdataHashIdConverter.GetJsonConverterAttribute(structuralProperty.DeclaringType.FullTypeName(), property.Name) is JsonHashIdConverterAttribute converterAttribute && converterAttribute != null)
            {
                var encoded = converterAttribute.Hashids.Encode(long.Parse(property.Value.ToString()));

                if (encoded != null)
                    property.Value = encoded;
            }

            return property;
        }
    }

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
                "(" + string.Join(",", newCollection.Select(y => (y as ConstantNode).Value)) + ")",
                nodeIn.CollectionType
            );
        }
    }

    public class HashIdQueryNodeVisitor : 
        QueryNodeVisitor<SingleValueNode>
    {
        public JsonHashIdConverterAttribute JsonConverterAttribute { get; set; }
        
        public override SingleValueNode Visit(ConstantNode nodeIn)
        {
            if (JsonConverterAttribute is JsonHashIdConverterAttribute converterAttribute && converterAttribute != null)
                return new ConstantNode(
                    $"'{converterAttribute.Hashids.Decode(nodeIn.Value.ToString())}'",
                    $"'{converterAttribute.Hashids.Decode(nodeIn.Value.ToString())}'"
                );

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

        private JsonHashIdConverterAttribute HasJsonConverterAttributeForProperty(SingleValuePropertyAccessNode propertyAccessNode)
        {
            var fullTypeName = propertyAccessNode.Property.DeclaringType.FullTypeName();
            var propertyName = propertyAccessNode.Property.Name;

            return OdataHashIdConverter.GetJsonConverterAttribute(fullTypeName, propertyName);
        }
    }
}
