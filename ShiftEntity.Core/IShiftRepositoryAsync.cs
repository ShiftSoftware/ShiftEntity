using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.TypeAuth.Core;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Core;

public interface IShiftRepositoryAsync<Entity, ListDTO, ViewAndUpsertDTO> :
        IShiftOdataList<Entity, ListDTO>,
        IShiftEntityFind<Entity>,
        IShiftEntityPrepareForReplicationAsync<Entity>,
        IShiftEntityViewAsync<Entity, ViewAndUpsertDTO>,
        IShiftEntityDeleteAsync<Entity>
        where Entity : ShiftEntity<Entity>, new()
        where ListDTO : ShiftEntityDTOBase
{

    void Add(Entity entity);
    Task<int> SaveChangesAsync();
    Message? ResponseMessage { get; set; }
    Dictionary<string, object>? AdditionalResponseData { get; set; }
    public ValueTask<Entity> UpsertAsync(
        Entity entity,
        ViewAndUpsertDTO dto,
        ActionTypes actionType,
        long? userId,
        Guid? idempotencyKey,
        bool disableDefaultDataLevelAccess,
        bool disableGlobalFilters
    );

    /// <summary>
    /// <see cref="UpsertAsync(Entity, ViewAndUpsertDTO, ActionTypes, long?, Guid?, bool, bool)"/> with the named
    /// <see cref="RepositoryBypass"/> vocabulary instead of the positional bool pair. A default implementation
    /// forwards to the bool member, so existing implementors get it for free.
    /// </summary>
    public ValueTask<Entity> UpsertAsync(
        Entity entity,
        ViewAndUpsertDTO dto,
        ActionTypes actionType,
        long? userId,
        Guid? idempotencyKey = null,
        RepositoryBypass bypass = RepositoryBypass.None)
        => UpsertAsync(entity, dto, actionType, userId, idempotencyKey,
            disableDefaultDataLevelAccess: bypass.HasFlag(RepositoryBypass.DataLevelAccess),
            disableGlobalFilters: bypass.HasFlag(RepositoryBypass.GlobalFilters));

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
    public bool CanAccessAs(Entity entity, Access access, ITypeAuthService typeAuth, ICurrentUserProvider user)
        => throw new NotSupportedException(
            $"{GetType().Name} cannot check its data-level rules for another user. Use a repository that implements CanAccessAs.");
}