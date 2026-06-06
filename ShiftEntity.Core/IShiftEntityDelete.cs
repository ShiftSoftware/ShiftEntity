using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Core;

public interface IShiftEntityDeleteAsync<EntityType> where EntityType : ShiftEntity<EntityType>
{
    // The flag parameter was historically named `isSoftDelete` here — the OPPOSITE of what every implementation
    // and caller names it (`isHardDelete`, the polarity actually passed). Renamed to match; no named-argument
    // caller of the old name exists (org-wide grep, 2026-06-06).
    public ValueTask<EntityType> DeleteAsync(
        EntityType entity,
        bool isHardDelete,
        long? userId,
        bool disableDefaultDataLevelAccess,
        bool disableGlobalFilters
    );

    /// <summary>
    /// <see cref="DeleteAsync(EntityType, bool, long?, bool, bool)"/> with the named <see cref="RepositoryBypass"/>
    /// vocabulary instead of the positional bool pair. A default implementation forwards to the bool member, so
    /// existing implementors get it for free.
    /// </summary>
    public ValueTask<EntityType> DeleteAsync(
        EntityType entity, bool isHardDelete, long? userId, RepositoryBypass bypass = RepositoryBypass.None)
        => DeleteAsync(entity, isHardDelete, userId,
            disableDefaultDataLevelAccess: bypass.HasFlag(RepositoryBypass.DataLevelAccess),
            disableGlobalFilters: bypass.HasFlag(RepositoryBypass.GlobalFilters));
}