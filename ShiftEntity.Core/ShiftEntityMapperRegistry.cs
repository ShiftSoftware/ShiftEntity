using System;
using System.Collections.Concurrent;
using System.Linq.Expressions;

namespace ShiftSoftware.ShiftEntity.Core;

/// <summary>
/// Runtime registry of source-generated mappers. Populated by module initializers the ShiftEntity
/// source generator emits. Two kinds:
/// - TRIPLE mappers, keyed by (entity, list DTO, view DTO) — consumed by
///   <c>ShiftRepositoryOptions.UseGeneratedMapper()</c> and endpoint discovery (<c>UseGeneratedMapper = true</c>).
/// - PAIR mappers (<see cref="IShiftObjectMapper{TEntity, TDto}"/>), keyed by (child entity, child DTO) —
///   consumed by the deep-mapping builder sugar (<c>ForEntityChildren</c>/<c>ForListChildren</c>). A pair's
///   conventions-only list projection expression is registered alongside it for SQL composition.
/// </summary>
public static class ShiftEntityMapperRegistry
{
    private static readonly ConcurrentDictionary<(Type Entity, Type ListDto, Type ViewDto), Type> mappers = new();
    private static readonly ConcurrentDictionary<(Type Entity, Type Dto), (Type MapperType, LambdaExpression? ListProjection)> pairs = new();

    public static void Register(Type entity, Type listDto, Type viewDto, Type mapperType)
        => mappers[(entity, listDto, viewDto)] = mapperType;

    public static Type? Find(Type entity, Type listDto, Type viewDto)
        => mappers.TryGetValue((entity, listDto, viewDto), out var mapperType) ? mapperType : null;

    public static void RegisterPair(Type entity, Type dto, Type mapperType, LambdaExpression? listProjection = null)
        => pairs[(entity, dto)] = (mapperType, listProjection);

    public static Type? FindPair(Type entity, Type dto)
        => pairs.TryGetValue((entity, dto), out var pair) ? pair.MapperType : null;

    public static LambdaExpression? FindPairListProjection(Type entity, Type dto)
        => pairs.TryGetValue((entity, dto), out var pair) ? pair.ListProjection : null;
}
