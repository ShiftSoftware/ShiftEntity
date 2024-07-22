using ShiftSoftware.ShiftEntity.Model.Dtos;
using System.Linq;

namespace ShiftSoftware.ShiftEntity.Core;

public interface IShiftOdataList<EntityType, ListDTO> 
    where ListDTO : ShiftEntityDTOBase
    where EntityType : class
{
    public IQueryable<ListDTO> OdataList(IQueryable<EntityType>? queryable = null);

    public IQueryable<EntityType> GetIQueryable();
}
