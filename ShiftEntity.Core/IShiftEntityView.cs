using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Core;

public interface IShiftEntityViewAsync<EntityType, DTOType>
    where EntityType : class
{
    public ValueTask<DTOType> ViewAsync(EntityType entity);
}
