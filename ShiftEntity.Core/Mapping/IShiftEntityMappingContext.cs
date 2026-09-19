namespace ShiftSoftware.ShiftEntity.Core.Mapping;

/// <summary>
/// The action a ShiftMapper map is running under — what <see cref="MappingContext.ActionType"/> is to an
/// <see cref="IShiftEntityMapper{TEntity, TListDTO, TViewDTO}"/>, for maps that have no context parameter.
///
/// <para>Scoped, and set by the repository around every map it runs: <see cref="ActionType"/> is
/// <c>Insert</c> or <c>Update</c> while a view DTO is being written onto an entity and null otherwise. A
/// mapper class or a repository's <c>Mapping(...)</c> lambda that needs to know injects this and reads it
/// inside the value delegate — <c>opt.MapFrom(d =&gt; context.ActionType == ActionTypes.Insert ? … : …)</c> —
/// never at declaration time, where it is always null.</para>
/// </summary>
public interface IShiftEntityMappingContext
{
    /// <summary>Insert or Update during a write map; null for reads, copies, and outside the repository.</summary>
    ActionTypes? ActionType { get; }
}
