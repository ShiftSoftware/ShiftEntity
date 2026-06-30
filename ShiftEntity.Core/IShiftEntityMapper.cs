using System;
using System.Linq;

namespace ShiftSoftware.ShiftEntity.Core;

public interface IShiftEntityMapper<TEntity, TListDTO, TViewDTO>
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
