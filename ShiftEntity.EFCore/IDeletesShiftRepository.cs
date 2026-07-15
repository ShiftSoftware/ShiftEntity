using System;
using System.Threading.Tasks;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Model.Dtos;

namespace ShiftSoftware.ShiftEntity.EFCore;

/// <summary>
/// Implement on an entity to take over the repository's delete WITHOUT hand-writing a repository class —
/// the exact analogue of overriding <c>ShiftRepository.DeleteAsync</c>, expressed on the entity. The signature is
/// that method's, verbatim, plus a trailing <see cref="ShiftRepositoryDeleteContext{TEntity, TListDTO, TViewDTO}"/>
/// carrying what an override would have reached through <c>this</c> and <c>base</c>:
/// <code>
/// public async ValueTask&lt;Country&gt; DeleteAsync(
///     Country entity, long? userId,
///     bool disableDefaultDataLevelAccess, bool disableGlobalFilters,
///     ShiftRepositoryDeleteContext&lt;Country, CountryDTO, CountryDTO&gt; context)
/// {
///     // ... custom logic before ...
///     var deleted = await context.Base();   // == base.DeleteAsync(entity, userId, …)
///     // ... custom logic after ...
///     return deleted;
/// }
/// </code>
/// Like an override, calling <c>Base()</c> is optional: skip it and you replace the default entirely — including
/// the protected-row guard, the soft-delete flag and the data-level delete check it performs.
/// <para>
/// Keyed by the endpoint's DTO triple — implement once per triple, exactly like
/// <see cref="IConfiguresShiftRepository{TEntity, TListDTO, TViewDTO}"/>.
/// </para>
/// <para>
/// <b>When it fires.</b> The hook is part of what <c>ShiftRepository.DeleteAsync</c> does, so ordinary override
/// semantics decide — there is no special case for custom repositories:
/// <list type="bullet">
/// <item>built-in repository (no repository class) ⇒ fires;</item>
/// <item>custom repository that doesn't override <c>DeleteAsync</c> ⇒ fires;</item>
/// <item>custom repository that overrides and calls <c>base.DeleteAsync(...)</c> ⇒ fires, nested inside the
/// override (repository outermost, then the entity, then the default);</item>
/// <item>custom repository that overrides WITHOUT calling base ⇒ does not fire — the base was replaced wholesale.</item>
/// </list>
/// So an entity's delete rule keeps applying even after someone adds a repository class for an unrelated reason.
/// (<see cref="IConfiguresShiftRepository{TEntity, TListDTO, TViewDTO}"/> differs here: it is built-in-only,
/// because a custom repository configures itself through its options builder and the two are alternatives.)
/// </para>
/// </summary>
public interface IDeletesShiftRepository<TEntity, TListDTO, TViewDTO>
    where TEntity : ShiftEntity<TEntity>, new()
    where TListDTO : ShiftEntityDTOBase
{
    /// <summary>
    /// Handle the delete. Return the deleted entity — normally whatever <c>context.Base()</c> returned.
    /// </summary>
    /// <param name="entity">The row being deleted, as loaded from the store. It has not been flagged deleted yet — <c>context.Base()</c> does that.</param>
    /// <param name="userId">The acting user, when the caller passed one. The default resolves it from the current user's claims when null.</param>
    /// <param name="disableDefaultDataLevelAccess">Whether this call opts out of data-level access.</param>
    /// <param name="disableGlobalFilters">Whether this call opts out of the global repository filters.</param>
    /// <param name="context">The extras an override would get for free: the request scope, the repository instance, and <c>Base()</c>.</param>
    ValueTask<TEntity> DeleteAsync(
        TEntity entity,
        long? userId,
        bool disableDefaultDataLevelAccess,
        bool disableGlobalFilters,
        ShiftRepositoryDeleteContext<TEntity, TListDTO, TViewDTO> context);
}

/// <summary>
/// The extras handed to <see cref="IDeletesShiftRepository{TEntity, TListDTO, TViewDTO}.DeleteAsync"/> alongside
/// the operation's own parameters: the shared
/// <see cref="ShiftRepositoryContext{TEntity, TListDTO, TViewDTO}.Services"/> /
/// <see cref="ShiftRepositoryContext{TEntity, TListDTO, TViewDTO}.Repository"/>, plus <see cref="Base()"/> —
/// the way back to the framework's default.
/// </summary>
public sealed class ShiftRepositoryDeleteContext<TEntity, TListDTO, TViewDTO>
    : ShiftRepositoryContext<TEntity, TListDTO, TViewDTO>
    where TEntity : ShiftEntity<TEntity>, new()
    where TListDTO : ShiftEntityDTOBase
{
    private readonly Func<TEntity, long?, bool, bool, ValueTask<TEntity>> baseDelete;

    // The arguments as the repository received them, so the parameterless Base() can replay them.
    private readonly TEntity entity;
    private readonly long? userId;
    private readonly bool disableDefaultDataLevelAccess;
    private readonly bool disableGlobalFilters;

    internal ShiftRepositoryDeleteContext(
        IServiceProvider services,
        IShiftRepositoryAsync<TEntity, TListDTO, TViewDTO> repository,
        TEntity entity,
        long? userId,
        bool disableDefaultDataLevelAccess,
        bool disableGlobalFilters,
        Func<TEntity, long?, bool, bool, ValueTask<TEntity>> baseDelete)
        : base(services, repository)
    {
        this.entity = entity;
        this.userId = userId;
        this.disableDefaultDataLevelAccess = disableDefaultDataLevelAccess;
        this.disableGlobalFilters = disableGlobalFilters;
        this.baseDelete = baseDelete;
    }

    /// <summary>
    /// Runs the framework's DEFAULT delete with the arguments this call received — the equivalent of
    /// <c>base.DeleteAsync(entity, userId, …)</c>: the protected-row guard, the data-level delete check, the
    /// soft-delete flag and audit stamping.
    /// <para>
    /// It replays the arguments AS RECEIVED. Reassigning a parameter inside the hook does not change what this
    /// runs (they are passed by value) — to feed the default different arguments, use the
    /// <see cref="Base(TEntity, long?, bool, bool)"/> overload.
    /// </para>
    /// Optional — skip it to replace the default entirely. Safe to call: it goes straight to the framework's
    /// implementation and never re-enters this hook.
    /// </summary>
    public ValueTask<TEntity> Base()
        => baseDelete(entity, userId, disableDefaultDataLevelAccess, disableGlobalFilters);

    /// <summary>
    /// Runs the framework's DEFAULT delete with the arguments YOU supply — the equivalent of calling
    /// <c>base.DeleteAsync(...)</c> with modified arguments (e.g. forcing a <paramref name="userId"/> or
    /// relaxing a bypass). See <see cref="Base()"/> for what the default does.
    /// </summary>
    public ValueTask<TEntity> Base(
        TEntity entity,
        long? userId,
        bool disableDefaultDataLevelAccess,
        bool disableGlobalFilters)
        => baseDelete(entity, userId, disableDefaultDataLevelAccess, disableGlobalFilters);
}
