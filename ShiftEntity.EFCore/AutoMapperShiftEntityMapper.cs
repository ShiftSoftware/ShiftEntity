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

    // AutoMapper resolves its own dependencies through its IMapper, so the service provider is unused here.
    public IQueryable<TListDTO> MapToList(IQueryable<TEntity> query, MappingContext context = default)
        => Mapper.ProjectTo<TListDTO>(query.AsNoTracking());

    public TViewDTO MapToView(TEntity entity, MappingContext context = default)
        => Mapper.Map<TViewDTO>(entity);

    public TEntity MapToEntity(TViewDTO dto, TEntity existing, MappingContext context = default)
        => Mapper.Map(dto, existing);

    public void CopyEntity(TEntity source, TEntity target, MappingContext context = default)
        => Mapper.Map(source, target);
}
