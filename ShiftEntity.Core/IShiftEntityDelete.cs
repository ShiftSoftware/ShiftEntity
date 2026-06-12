using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Core;

public interface IShiftEntityDeleteAsync<EntityType> where EntityType : ShiftEntity<EntityType>
{
    // Deletion is always a SOFT delete: the framework flags IsDeleted and stamps the deleter; rows are never
    // removed from the database. (A hard-delete flag existed historically but had been a silent no-op since
    // 2025-08; it was removed together with the deleted-row replication log.)
    public ValueTask<EntityType> DeleteAsync(
        EntityType entity,
        long? userId,
        bool disableDefaultDataLevelAccess,
        bool disableGlobalFilters
    );

    /// <summary>
    /// <see cref="DeleteAsync(EntityType, long?, bool, bool)"/> with the named <see cref="RepositoryBypass"/>
    /// vocabulary instead of the positional bool pair. A default implementation forwards to the bool member, so
    /// existing implementors get it for free.
    /// </summary>
    public ValueTask<EntityType> DeleteAsync(
        EntityType entity, long? userId, RepositoryBypass bypass = RepositoryBypass.None)
        => DeleteAsync(entity, userId,
            disableDefaultDataLevelAccess: bypass.HasFlag(RepositoryBypass.DataLevelAccess),
            disableGlobalFilters: bypass.HasFlag(RepositoryBypass.GlobalFilters));
}
