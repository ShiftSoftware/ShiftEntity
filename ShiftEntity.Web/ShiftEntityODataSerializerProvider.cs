using Microsoft.AspNetCore.OData.Formatter;
using Microsoft.AspNetCore.OData.Formatter.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OData;
using Microsoft.OData.Edm;
using ShiftSoftware.ShiftEntity.Model.HashIds;
using ShiftSoftware.ShiftEntity.Web.Services;
using System;

namespace ShiftSoftware.ShiftEntity.Web;

class ShiftEntityODataSerializerProvider : ODataSerializerProvider
{
    private readonly IServiceProvider serviceProvider;

    public ShiftEntityODataSerializerProvider(IServiceProvider serviceProvider) : base(serviceProvider)
    {
        this.serviceProvider = serviceProvider;
    }

    public override IODataEdmTypeSerializer GetEdmTypeSerializer(IEdmTypeReference edmType)
    {
        if (edmType.Definition.TypeKind == EdmTypeKind.Entity)
            return new ShiftEntityODataResourceSerializer(this, serviceProvider.GetRequiredService<TimeZoneService>());
        else
            return base.GetEdmTypeSerializer(edmType);
    }
}

class ShiftEntityODataResourceSerializer : ODataResourceSerializer
{
    private readonly TimeZoneService timeZoneService;

    public ShiftEntityODataResourceSerializer(ODataSerializerProvider serializerProvider,
        TimeZoneService timeZoneService) 
        : base(serializerProvider)
    {
        this.timeZoneService = timeZoneService;
    }

    public override ODataProperty CreateStructuralProperty(IEdmStructuralProperty structuralProperty, ResourceContext resourceContext)
    {
        ODataProperty property = base.CreateStructuralProperty(structuralProperty, resourceContext);

        if (HashId.Enabled || HashId.IdentityHashIdEnabled)
        {
            if (property.Value != null && OdataHashIdConverter.GetJsonConverterAttribute(structuralProperty.DeclaringType.FullTypeName(), property.Name) is JsonHashIdConverterAttribute converterAttribute && converterAttribute != null)
            {
                var encoded = converterAttribute.Hashids?.Encode(long.Parse(property.Value.ToString()));

                if (encoded != null)
                    property.Value = encoded;
            }
        }

        if (property?.Value?.GetType() == typeof(DateTimeOffset))
        {
            var dateTime = (DateTimeOffset)property.Value;

            if (dateTime != default)
            {
                //Substracting or adding may result in invalid dates.
                //For example if the value is Datetime.Min or Datetime.Max
                try
                {
                    dateTime = new DateTimeOffset(timeZoneService.WriteOffsettedDate(dateTime), timeZoneService.GetTimeZoneOffset());
                }
                catch
                {

                }

                property.Value = dateTime;
            }
        }

        return property;
    }
}
