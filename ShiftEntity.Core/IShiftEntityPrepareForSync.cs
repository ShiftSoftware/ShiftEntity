using ShiftSoftware.ShiftEntity.Model.Enums;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Core;

public interface IShiftEntityPrepareForReplicationAsync<EntityType> where EntityType : class
{
    public ValueTask<EntityType> PrepareForReplicationAsync(EntityType entity, ReplicationChangeType changeType)
    {
        return new ValueTask<EntityType>(entity);
    }
}
