using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ShiftSoftware.ShiftEntity.Web.Pii;

// Protected children are matched by the framework's own IDs: ShiftEntityDTOBase.ID on the DTO side and
// ShiftEntityBase.ID on the entity side. The JSON converter has already decoded a submitted DTO ID from its
// hash, so both sides hold the same number. A new child has no ID yet.
internal static class PiiGraphIdentity
{
    public static bool HasIds(Type dtoType, Type entityType)
        => typeof(ShiftEntityDTOBase).IsAssignableFrom(dtoType) && typeof(ShiftEntityBase).IsAssignableFrom(entityType);

    public static long? DtoId(object? dto)
        => dto is ShiftEntityDTOBase { ID: { } id } && long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            && value > 0 ? value : null;

    public static long? EntityId(object? entity)
        => entity is ShiftEntityBase { ID: > 0 } shiftEntity ? shiftEntity.ID : null;

    public static Type? ElementType(Type type)
        => type.IsArray ? type.GetElementType()
            : type.GetInterfaces().Append(type)
                .FirstOrDefault(x => x.IsGenericType && x.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                ?.GetGenericArguments()[0];

    public static object[] Items(object? value)
        => value is IEnumerable items ? items.Cast<object>().ToArray() : Array.Empty<object>();
}
