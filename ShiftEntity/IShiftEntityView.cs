using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Core
{
    public interface IShiftEntityView<EntityType, ViewDTOType>
        where EntityType: class
    {
        public ViewDTOType View(EntityType entity);
    }

    public interface IShiftEntityViewAsync<EntityType, ViewDTOType>
        where EntityType : class
    {
        public Task<ViewDTOType> ViewAsync(EntityType entity);
    }
}
