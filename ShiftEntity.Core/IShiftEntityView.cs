using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Core
{
    public interface IShiftEntityView<EntityType, DTOType>
        where EntityType: class
    {
        public DTOType View(EntityType entity);
    }

    public interface IShiftEntityViewAsync<EntityType, DTOType>
        where EntityType : class
    {
        public ValueTask<DTOType> ViewAsync(EntityType entity);
    }
}
