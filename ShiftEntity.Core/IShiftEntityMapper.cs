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
    // Each method receives a MappingContext carrying the service provider (so a mapper can resolve services
    // on demand — a lookup/localization service — instead of constructor-injecting them) plus the action
    // being performed when known. This lets a mapper stay unregistered — plugged via
    // options.UseMapper(new MyMapper()) — yet still reach DI. The repository passes its DbContext's
    // application service provider when it calls these; a bare IServiceProvider converts implicitly.
    TViewDTO MapToView(TEntity entity, MappingContext context = default);
    TEntity MapToEntity(TViewDTO dto, TEntity existing, MappingContext context = default);
    IQueryable<TListDTO> MapToList(IQueryable<TEntity> query, MappingContext context = default);
    void CopyEntity(TEntity source, TEntity target, MappingContext context = default);
}
