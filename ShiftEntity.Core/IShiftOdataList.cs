using ShiftSoftware.ShiftEntity.Model.Dtos;
using System.Linq;

namespace ShiftSoftware.ShiftEntity.Core
{
    public interface IShiftOdataList<ListDTO> where ListDTO : ShiftEntityDTOBase
    {
        public IQueryable<ListDTO> OdataList(bool showDeletedRows = false);
    }
}
