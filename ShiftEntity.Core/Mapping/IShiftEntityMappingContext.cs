namespace ShiftSoftware.ShiftEntity.Core.Mapping;

/// <summary>
/// The action a ShiftMapper map is running under — what <see cref="MappingContext.ActionType"/> is to an
/// <see cref="IShiftEntityMapper{TEntity, TListDTO, TViewDTO}"/>, for maps that have no context parameter.
///
/// <para>Scoped, and set by the repository around every map it runs: <see cref="ActionType"/> is
/// <c>Insert</c> or <c>Update</c> while a view DTO is being written onto an entity and null otherwise. A
/// mapper class that needs to know reads it from <c>Services</c> inside the value delegate —
/// <c>opt.MapFrom(d =&gt; context.Value.ActionType == ActionTypes.Insert ? … : …)</c> over a
/// <c>Lazy&lt;IShiftEntityMappingContext&gt;</c> — never at declaration time, where it is always null.</para>
/// </summary>
public interface IShiftEntityMappingContext
{
    /// <summary>Insert or Update during a write map; null for reads, copies, and outside the repository.</summary>
    ActionTypes? ActionType { get; }
}
