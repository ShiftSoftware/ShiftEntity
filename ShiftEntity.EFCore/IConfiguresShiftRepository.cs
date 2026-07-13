using System;
using ShiftSoftware.ShiftEntity.Core;

namespace ShiftSoftware.ShiftEntity.EFCore;

/// <summary>
/// Implement on an entity to configure the BUILT-IN repository that attribute-driven CRUD endpoints
/// (<c>[ShiftEntityEndpoint&lt;...&gt;]</c>) use — for small needs such as including related entities or a
/// small mapping tweak — WITHOUT hand-writing a repository class. The auto-CRUD calls
/// <see cref="ConfigureRepository"/> when it builds the repository options, handing you a
/// <see cref="ShiftRepositoryConfigurationContext{TEntity, TListDTO, TViewDTO}"/> that carries the options
/// and the request-scoped <see cref="IServiceProvider"/> (so the configuration can resolve scoped services
/// such as the current user, tenant, localization, …).
/// <para>
/// Keyed by the endpoint's DTO triple: an entity exposing several endpoints (different List/View DTOs)
/// implements this once per triple — e.g. <c>Country : IConfiguresShiftRepository&lt;Country, CountryDTO,
/// CountryDTO&gt;, IConfiguresShiftRepository&lt;Country, CountryMappedDTO, CountryMappedDTO&gt;</c> — and the
/// implementation matching the endpoint being served is selected automatically.
/// </para>
/// <para>
/// This hook runs ONLY on the built-in repository path. Supplying a custom repository
/// (<c>[ShiftEntityEndpoint&lt;..., TRepository&gt;]</c>) or a manual options builder takes over instead, so
/// the hook does not run in those cases — configure there.
/// </para>
/// </summary>
public interface IConfiguresShiftRepository<TEntity, TListDTO, TViewDTO>
    where TEntity : ShiftEntity<TEntity>
{
    /// <summary>
    /// Configure the repository options for this entity's endpoint. Called once per repository construction
    /// (i.e. per request), so <see cref="ShiftRepositoryConfigurationContext{TEntity, TListDTO, TViewDTO}.Services"/>
    /// is the request scope.
    /// </summary>
    void ConfigureRepository(ShiftRepositoryConfigurationContext<TEntity, TListDTO, TViewDTO> context);
}

/// <summary>
/// The arguments handed to <see cref="IConfiguresShiftRepository{TEntity, TListDTO, TViewDTO}.ConfigureRepository"/>.
/// A context (rather than loose parameters) so more can be added later — e.g. the action being served — without
/// breaking existing implementers.
/// </summary>
public sealed class ShiftRepositoryConfigurationContext<TEntity, TListDTO, TViewDTO>
    where TEntity : ShiftEntity<TEntity>
{
    public ShiftRepositoryConfigurationContext(
        ShiftRepositoryOptions<TEntity, TListDTO, TViewDTO> options, IServiceProvider services)
    {
        Options = options;
        Services = services;
    }

    /// <summary>The repository options to configure — includes, mapping (UseMapper/UseGeneratedMapper), filters, data-level access, ….</summary>
    public ShiftRepositoryOptions<TEntity, TListDTO, TViewDTO> Options { get; }

    /// <summary>The request-scoped service provider. Resolve scoped services here (current user, tenant, localization, …).</summary>
    public IServiceProvider Services { get; }
}
