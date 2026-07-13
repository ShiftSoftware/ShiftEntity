using System;

namespace ShiftSoftware.ShiftEntity.Core;

/// <summary>
/// The ambient information handed to the mapping methods (<see cref="IShiftEntityMapper{TEntity, TListDTO, TViewDTO}"/>
/// and <see cref="IShiftObjectMapper{TEntity, TDto}"/>). A <c>readonly struct</c> (allocation-free on the hot
/// mapping path) so more can be added later — e.g. the acting user — WITHOUT changing every mapper signature.
/// A bare service provider is wrapped with <c>new MappingContext(services)</c>.
/// </summary>
public readonly struct MappingContext
{
    public MappingContext(IServiceProvider? services, ActionTypes? actionType = null)
    {
        Services = services;
        ActionType = actionType;
    }

    /// <summary>The service provider for resolving services on demand (localization, lookups, current user, …). May be null.</summary>
    public IServiceProvider? Services { get; }

    /// <summary>
    /// The action being performed (Insert/Update) when known — e.g. in <c>MapToEntity</c> during an upsert.
    /// Null for reads/copies or when the caller didn't supply it.
    /// </summary>
    public ActionTypes? ActionType { get; }

    /// <summary>
    /// Returns this context with <see cref="Services"/> filled from <paramref name="fallback"/> when it has
    /// none (keeping <see cref="ActionType"/>). Used by the repository to supply its own service provider
    /// when a caller didn't.
    /// </summary>
    public MappingContext WithFallbackServices(IServiceProvider? fallback)
        => Services is null ? new MappingContext(fallback, ActionType) : this;
}
