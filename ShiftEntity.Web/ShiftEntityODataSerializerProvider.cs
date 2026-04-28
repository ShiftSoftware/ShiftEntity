using Microsoft.AspNetCore.OData.Formatter;
using Microsoft.AspNetCore.OData.Formatter.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OData;
using Microsoft.OData.Edm;
using ShiftSoftware.ShiftEntity.Core;
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
            return new ShiftEntityODataResourceSerializer(this, serviceProvider.GetRequiredService<IHashIdService>());
        else
            return base.GetEdmTypeSerializer(edmType);
    }
}

class ShiftEntityODataResourceSerializer : ODataResourceSerializer
{
    private readonly IHashIdService hashIdService;

    public ShiftEntityODataResourceSerializer(ODataSerializerProvider serializerProvider, IHashIdService hashIdService)
        : base(serializerProvider)
    {
        this.hashIdService = hashIdService;
    }

    public override ODataProperty CreateStructuralProperty(IEdmStructuralProperty structuralProperty, ResourceContext resourceContext)
    {
        ODataProperty property = base.CreateStructuralProperty(structuralProperty, resourceContext);

        if (hashIdService.Enabled || hashIdService.IdentityHashIdEnabled)
        {
            if (property.Value != null)
            {
                // Resolve the CLR property on the underlying resource type to locate the
                // [JsonHashIdConverterAttribute]. The structural property's declaring EDM type
                // doesn't always map cleanly back to a CLR type, so we walk via the resource's
                // EdmType and its ClrType annotation via the resource context.
                var clrType = resourceContext.StructuredType?.FullTypeName() is string clrTypeName
                    ? Type.GetType(clrTypeName)
                    : null;

                // Fallback: use the ResourceInstance's runtime type.
                clrType ??= resourceContext.ResourceInstance?.GetType();

                if (clrType != null)
                {
                    var converterAttribute = HashIdConverterAttributeLookup.Get(clrType, property.Name);
                    if (converterAttribute != null)
                    {
                        var encoded = hashIdService.Encode(long.Parse(property.Value.ToString()!), converterAttribute);
                        property.Value = encoded;
                    }
                }
            }
        }

        return property;
    }
}
