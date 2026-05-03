using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.ShiftEntity.Model.HashIds;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace ShiftSoftware.ShiftEntity.Web.Services;

/// <summary>
/// Swaps the JSON converter on each property decorated with <see cref="JsonHashIdConverterAttribute"/>
/// for a DI-aware instance constructed with a live <see cref="IHashIdService"/>. Runs at
/// <c>JsonTypeInfo</c> build time (first serialization of a given type), which happens after DI is
/// fully configured — so it isn't subject to the attribute-construction-time race that affected
/// the legacy static path.
/// </summary>
internal static class HashIdJsonTypeInfoResolverModifier
{
    public static Action<JsonTypeInfo> Create(IHashIdService hashIdService)
    {
        return typeInfo =>
        {
            if (typeInfo.Kind != JsonTypeInfoKind.Object) return;

            foreach (var prop in typeInfo.Properties)
            {
                var attr = prop.AttributeProvider?
                    .GetCustomAttributes(typeof(JsonHashIdConverterAttribute), inherit: true)
                    .OfType<JsonHashIdConverterAttribute>()
                    .FirstOrDefault();

                if (attr is null) continue;

                var hasher = hashIdService.GetHasherFor(attr);
                var converter = BuildConverter(prop.PropertyType, hasher, attr.ConfigurationName, hashIdService);
                if (converter is not null)
                    prop.CustomConverter = converter;
            }
        };
    }

    private static JsonConverter? BuildConverter(Type propertyType, ShiftEntityHashId? hasher, string? configurationName, IHashIdService hashIdService)
    {
        if (propertyType == typeof(string))
            return new StringJsonHashIdConverter(hasher, configurationName, hashIdService);
        if (propertyType == typeof(long))
            return new LongJsonHashIdConverter(hasher, configurationName, hashIdService);
        if (propertyType == typeof(long?))
            return new NullableLongJsonHashIdConverter(hasher, configurationName, hashIdService);
        if (propertyType == typeof(ShiftEntitySelectDTO))
            return new ShiftEntitySelectDTOJsonHashIdConverter(hasher, configurationName, hashIdService);
        if (propertyType == typeof(IEnumerable<ShiftEntitySelectDTO>))
            return new ShiftEntitySelectDTOEnumerableJsonHashIdConverter(hasher, configurationName, hashIdService);
        if (propertyType == typeof(List<ShiftEntitySelectDTO>))
            return new ShiftEntitySelectDTOListJsonHashIdConverter(hasher, configurationName, hashIdService);

        return null;
    }
}
