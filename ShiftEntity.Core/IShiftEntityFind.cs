using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.TypeAuth.Core;
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

    /// <summary>
    /// Whether the user that <paramref name="user"/> and <paramref name="typeAuth"/> describe passes this
    /// repository's data-level rules for <paramref name="entity"/> at <paramref name="access"/>: the same rules the
    /// repository applies to its own reads and writes for the signed-in user, checked for another user. It checks
    /// data-level access only. The caller checks the user's screen permission itself, with <c>typeAuth.Can(...)</c>.
    /// <para>
    /// A repository that cannot answer for another user must refuse. This default does: it throws
    /// <see cref="NotSupportedException"/>.
    /// </para>
    /// </summary>
    public bool CanAccessAs(EntityType entity, Access access, ITypeAuthService typeAuth, ICurrentUserProvider user)
        => throw new NotSupportedException(
            $"{GetType().Name} cannot check its data-level rules for another user. Use a repository that implements CanAccessAs.");
}