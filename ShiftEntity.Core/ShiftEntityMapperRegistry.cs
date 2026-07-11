using System;
using System.Collections.Concurrent;

namespace ShiftSoftware.ShiftEntity.Core;

/// <summary>
/// Runtime registry of source-generated mappers, keyed by the (entity, list DTO, view DTO) triple.
/// Populated by module initializers the ShiftEntity source generator emits alongside every
/// <c>[ShiftEntityMapper]</c> class. Consumed by <c>ShiftRepositoryOptions.UseGeneratedMapper()</c>
/// and by endpoint discovery for attributes marked <c>UseGeneratedMapper = true</c>.
/// </summary>
public static class ShiftEntityMapperRegistry
{
    private static readonly ConcurrentDictionary<(Type Entity, Type ListDto, Type ViewDto), Type> mappers = new();

    public static void Register(Type entity, Type listDto, Type viewDto, Type mapperType)
        => mappers[(entity, listDto, viewDto)] = mapperType;

    public static Type? Find(Type entity, Type listDto, Type viewDto)
        => mappers.TryGetValue((entity, listDto, viewDto), out var mapperType) ? mapperType : null;
}
