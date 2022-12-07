using System.Linq;

namespace ShiftSoftware.ShiftEntity.Core
{
    public interface IShiftOdataList<ListDTO>
    {
        public IQueryable<ListDTO> OdataList();
    }
}
