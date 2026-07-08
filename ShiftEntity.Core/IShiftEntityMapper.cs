using System;
using System.Linq;

namespace ShiftSoftware.ShiftEntity.Core;

/// <summary>
/// Non-generic marker for the mapper family. Its only purpose is to let generic parameters be
/// constrained to "a ShiftEntity mapper" without knowing the entity/DTO type arguments — e.g. the
/// <c>TMapper</c> of <c>ShiftEntityEndpointWithMapperAttribute&lt;…, TMapper&gt;</c>. The exact
/// <c>(entity, list, view)</c> triple is validated separately (at endpoint discovery).
/// </summary>
public interface IShiftEntityMapper { }

public interface IShiftEntityMapper<TEntity, TListDTO, TViewDTO> : IShiftEntityMapper
{
    // Each method receives an optional IServiceProvider so a mapper can resolve services on demand
    // (e.g. a lookup/localization service) instead of having them constructor-injected. This lets a
    // mapper stay unregistered — plugged via options.UseMapper(new MyMapper()) — yet still reach DI.
    // The repository passes its DbContext's application service provider when it calls these; it is
    // null only when a mapper is invoked directly without one.
    TViewDTO MapToView(TEntity entity, IServiceProvider? serviceProvider = null);
    TEntity MapToEntity(TViewDTO dto, TEntity existing, IServiceProvider? serviceProvider = null);
    IQueryable<TListDTO> MapToList(IQueryable<TEntity> query, IServiceProvider? serviceProvider = null);
    void CopyEntity(TEntity source, TEntity target, IServiceProvider? serviceProvider = null);
}
