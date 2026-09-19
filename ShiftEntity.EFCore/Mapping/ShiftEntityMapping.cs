using ShiftMapper;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Model.Dtos;

namespace ShiftSoftware.ShiftEntity.EFCore;

/// <summary>
/// Where a repository customizes its automatic maps — the argument of
/// <see cref="ShiftRepositoryOptions{EntityType, ListDTO, ViewAndUpsertDTO}.Mapping"/>.
///
/// <code>
/// o.Mapping(m =&gt; m.List.ForMember(d =&gt; d.Total, opt =&gt; opt.MapFrom(i =&gt; i.Lines.Sum(l =&gt; l.Amount))));
/// o.Mapping(m =&gt; m.View.ForMember(d =&gt; d.Secret, opt =&gt; opt.Ignore()));
/// </code>
///
/// <para>Four handles, one per map the repository declares, each the same <see cref="MapExpression{TSource, TDestination}"/>
/// a <c>CreateMap</c> returns — <c>ForMember</c>, <c>MapFrom</c>, <c>Ignore</c>, <c>AfterMap</c>, the ShiftMapper
/// vocabulary. The generator reads the lambda for its SHAPE and bakes it into the repository's implicit maps at
/// build time; the value delegates stay at run time in this surface, which the repository hands to the mapper
/// (<see cref="IMapper.Configure"/>) when it is constructed — and which the mapper pulls in by constructing the
/// repository from DI when a customized map is used before any repository ran.</para>
///
/// <para><see cref="ShiftMapperConfigurationSurface.Nested"/> caps how deep the child maps below these go
/// (default 10).</para>
/// </summary>
public sealed class ShiftEntityMapping<EntityType, ListDTO, ViewAndUpsertDTO> : ShiftMapperConfigurationSurface
    where EntityType : ShiftEntity<EntityType>
{
    /// <summary>Entity → view DTO: what <c>MapToView</c> runs.</summary>
    public MapExpression<EntityType, ViewAndUpsertDTO> View => Map<EntityType, ViewAndUpsertDTO>();

    /// <summary>View DTO → entity: what <c>MapToEntity</c> runs, onto the existing (or new) entity.</summary>
    public MapExpression<ViewAndUpsertDTO, EntityType> Entity => Map<ViewAndUpsertDTO, EntityType>();

    /// <summary>Entity → list DTO, as a projection the database runs: what <c>MapToList</c> runs.</summary>
    public MapExpression<EntityType, ListDTO> List => Map<EntityType, ListDTO>();

    /// <summary>Entity → entity: what <c>CopyEntity</c> runs to refresh a tracked row after a save.</summary>
    public MapExpression<EntityType, EntityType> Copy => Map<EntityType, EntityType>();
}
