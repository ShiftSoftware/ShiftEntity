using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Core;

public interface IShiftEntityPrepareForSyncAsync<EntityType> where EntityType : ShiftEntity<EntityType>
{
    public ValueTask<EntityType> PrepareForSyncAsync(EntityType entity, SyncChangeType changeType)
    {
        return new ValueTask<EntityType>(entity);
    }
}

public enum SyncChangeType
{
    Added,
    Modified,
    Deleted,
}
