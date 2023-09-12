using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Core;

public interface IShiftEntityPrepareForReplicationAsync<EntityType> where EntityType : ShiftEntity<EntityType>
{
    public ValueTask<EntityType> PrepareForReplicationAsync(EntityType entity, ReplicationChangeType changeType)
    {
        return new ValueTask<EntityType>(entity);
    }
}

public enum ReplicationChangeType
{
    Added,
    Modified,
    Deleted,
}
