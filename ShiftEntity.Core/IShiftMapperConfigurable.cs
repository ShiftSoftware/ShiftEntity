using System;

namespace ShiftSoftware.ShiftEntity.Core;

/// <summary>
/// Implemented by every SOURCE-GENERATED mapper. Lets external code — the repository's
/// <c>UseGeneratedMapper(configure)</c> overload — append per-property customizations to the mapper's
/// <see cref="ShiftMapperBuilder{TEntity, TListDTO, TViewDTO}"/> after the mapper's own <c>Configure</c>
/// partial hook has run (later registrations win, so the use site overrides the shared mapper config).
/// </summary>
public interface IShiftMapperConfigurable<TEntity, TListDTO, TViewDTO>
{
    void AddConfiguration(Action<ShiftMapperBuilder<TEntity, TListDTO, TViewDTO>> configure);
}
