using ShiftSoftware.ShiftEntity.Model.Dtos;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Core;

public interface IShiftEntityFind<EntityType> where EntityType : ShiftEntity<EntityType>
{
    public Task<EntityType?> FindAsync(
        long id,
        DateTimeOffset? asOf,
        bool disableDefaultDataLevelAccess,
        bool disableGlobalFilters
    );
    public Task<EntityType?> FindByIdempotencyKeyAsync(
        Guid idempotencyKey,
        DateTimeOffset? asOf,
        bool disableDefaultDataLevelAccess,
        bool disableGlobalFilters
    );

    /// <summary>
    /// <see cref="FindAsync(long, DateTimeOffset?, bool, bool)"/> with the named <see cref="RepositoryBypass"/>
    /// vocabulary instead of the positional bool pair. A default implementation forwards to the bool member, so
    /// existing implementors get it for free.
    /// </summary>
    public Task<EntityType?> FindAsync(long id, DateTimeOffset? asOf = null, RepositoryBypass bypass = RepositoryBypass.None)
        => FindAsync(id, asOf,
            disableDefaultDataLevelAccess: bypass.HasFlag(RepositoryBypass.DataLevelAccess),
            disableGlobalFilters: bypass.HasFlag(RepositoryBypass.GlobalFilters));

    /// <summary>
    /// <see cref="FindByIdempotencyKeyAsync(Guid, DateTimeOffset?, bool, bool)"/> with the named
    /// <see cref="RepositoryBypass"/> vocabulary instead of the positional bool pair. A default implementation
    /// forwards to the bool member, so existing implementors get it for free.
    /// </summary>
    public Task<EntityType?> FindByIdempotencyKeyAsync(Guid idempotencyKey, DateTimeOffset? asOf = null, RepositoryBypass bypass = RepositoryBypass.None)
        => FindByIdempotencyKeyAsync(idempotencyKey, asOf,
            disableDefaultDataLevelAccess: bypass.HasFlag(RepositoryBypass.DataLevelAccess),
            disableGlobalFilters: bypass.HasFlag(RepositoryBypass.GlobalFilters));

    public IQueryable<RevisionDTO> GetRevisionsAsync(long id);
    public Task<Stream> PrintAsync(string id);
}