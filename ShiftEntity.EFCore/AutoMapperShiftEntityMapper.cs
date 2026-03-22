using AutoMapper;
using Microsoft.EntityFrameworkCore;
using ShiftSoftware.ShiftEntity.Core;

namespace ShiftSoftware.ShiftEntity.EFCore;

public class AutoMapperShiftEntityMapper<TEntity, TListDTO, TViewDTO>
    : IShiftEntityMapper<TEntity, TListDTO, TViewDTO>
    where TEntity : class
{
    public IMapper Mapper { get; }

    public AutoMapperShiftEntityMapper(IMapper mapper)
    {
        Mapper = mapper;
    }

    public IQueryable<TListDTO> MapToList(IQueryable<TEntity> query)
        => Mapper.ProjectTo<TListDTO>(query.AsNoTracking());

    public TViewDTO MapToView(TEntity entity)
        => Mapper.Map<TViewDTO>(entity);

    public TEntity MapToEntity(TViewDTO dto, TEntity existing)
        => Mapper.Map(dto, existing);

    public void CopyEntity(TEntity source, TEntity target)
        => Mapper.Map(source, target);
}
