using System.Linq;

namespace ShiftSoftware.ShiftEntity.Core;

public interface IShiftEntityMapper<TEntity, TListDTO, TViewDTO>
{
    TViewDTO MapToView(TEntity entity);
    TEntity MapToEntity(TViewDTO dto, TEntity existing);
    IQueryable<TListDTO> MapToList(IQueryable<TEntity> query);
    void CopyEntity(TEntity source, TEntity target);
}
