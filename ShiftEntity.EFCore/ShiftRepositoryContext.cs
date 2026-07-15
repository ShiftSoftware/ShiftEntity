using System;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Model.Dtos;

namespace ShiftSoftware.ShiftEntity.EFCore;

/// <summary>
/// The shared arguments handed to every entity-driven repository hook — the base of
/// <see cref="ShiftRepositoryConfigurationContext{TEntity, TListDTO, TViewDTO}"/>,
/// <see cref="ShiftRepositoryUpsertContext{TEntity, TListDTO, TViewDTO}"/> and
/// <see cref="ShiftRepositoryDeleteContext{TEntity, TListDTO, TViewDTO}"/>. Each hook's own context adds only
/// what that operation needs, so a hook never sees a member that doesn't apply to it.
/// <para>
/// A context (rather than loose parameters) so more can be added later — e.g. the action being served — without
/// breaking existing implementers.
/// </para>
/// </summary>
public abstract class ShiftRepositoryContext<TEntity, TListDTO, TViewDTO>
    where TEntity : ShiftEntity<TEntity>, new()
    where TListDTO : ShiftEntityDTOBase
{
    protected ShiftRepositoryContext(
        IServiceProvider services, IShiftRepositoryAsync<TEntity, TListDTO, TViewDTO> repository)
    {
        Services = services;
        Repository = repository;
    }

    /// <summary>
    /// The request-scoped service provider. Resolve scoped services here (current user, tenant, localization, …).
    /// </summary>
    public IServiceProvider Services { get; }

    /// <summary>
    /// The repository instance serving this call — the same object a custom repository would reach through
    /// <c>this</c>. Use it for the repository's OTHER operations (<c>FindAsync</c>, <c>GetIQueryable</c>,
    /// <c>SaveChangesAsync</c>, <c>ResponseMessage</c>, …).
    /// <para>
    /// To run the DEFAULT behavior of the operation being hooked, call that context's <c>Base()</c> —
    /// <b>not</b> the matching method on this property. <c>Repository.UpsertAsync(…)</c> re-enters the hook
    /// and recurses forever; <c>Base()</c> goes straight to the framework's implementation.
    /// </para>
    /// <para>
    /// During <see cref="IConfiguresShiftRepository{TEntity, TListDTO, TViewDTO}.ConfigureRepository"/> the
    /// repository is still being constructed (its mapper isn't resolved yet), so hold the reference rather than
    /// calling operations on it there. It is fully initialized by the time the upsert/delete hooks run.
    /// </para>
    /// </summary>
    public IShiftRepositoryAsync<TEntity, TListDTO, TViewDTO> Repository { get; }
}
