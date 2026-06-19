using ShiftSoftware.ShiftEntity.Model.Dtos;
using System;

namespace ShiftSoftware.ShiftEntity.Core;

/// <summary>
/// Base for the attribute-driven CRUD endpoint declarations placed on entity classes.
///
/// The framework scans entities for these attributes (<see cref="ShiftEntityEndpointDiscovery"/>)
/// and auto-generates the minimal-API CRUD endpoints + the default AutoMapper map, so an entity
/// needs no controller and — unless it has a custom repository — no repository class either.
///
/// Use the generic <c>ShiftEntityEndpointAttribute&lt;…&gt;</c> (anonymous) or
/// <c>ShiftEntitySecureEndpointAttribute&lt;…&gt;</c> (RequireAuthorization + per-verb TypeAuth)
/// variants; the custom-repository forms add the repository as a trailing generic parameter.
/// All variants allow multiple, so one entity can expose several endpoints.
/// </summary>
public abstract class ShiftEntityEndpointAttributeBase : Attribute
{
    /// <summary>The route prefix the CRUD endpoints are mapped under (e.g. <c>"api/country"</c>).</summary>
    public string Route { get; }

    /// <summary>True for the secure variants (RequireAuthorization + per-verb TypeAuth).</summary>
    public abstract bool IsSecure { get; }

    /// <summary>
    /// For secure variants: the static field name of the action node on the action tree
    /// (e.g. <c>nameof(StockPlusPlusActionTree.Country)</c>). The named field must be a
    /// <c>ReadWriteDeleteAction</c> declared directly on <c>TActionTree</c> (not a dynamic /
    /// data-level action). Null for anonymous variants.
    /// </summary>
    public virtual string? ActionName => null;

    protected ShiftEntityEndpointAttributeBase(string route)
    {
        Route = route;
    }
}

/// <summary>
/// Anonymous CRUD endpoints over the framework's built-in repository.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class ShiftEntityEndpointAttribute<TListDTO, TViewDTO> : ShiftEntityEndpointAttributeBase
    where TListDTO : ShiftEntityDTOBase
    where TViewDTO : ShiftEntityViewAndUpsertDTO
{
    public override bool IsSecure => false;

    public ShiftEntityEndpointAttribute(string route) : base(route) { }
}

/// <summary>
/// Anonymous CRUD endpoints using a custom repository (<typeparamref name="TRepository"/>).
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class ShiftEntityEndpointAttribute<TListDTO, TViewDTO, TRepository> : ShiftEntityEndpointAttributeBase
    where TListDTO : ShiftEntityDTOBase
    where TViewDTO : ShiftEntityViewAndUpsertDTO
    where TRepository : ShiftRepositoryBase
{
    public override bool IsSecure => false;

    public ShiftEntityEndpointAttribute(string route) : base(route) { }
}

/// <summary>
/// Secure CRUD endpoints (RequireAuthorization + per-verb TypeAuth using the
/// <paramref name="actionName"/> node on <typeparamref name="TActionTree"/>) over the built-in repository.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class ShiftEntitySecureEndpointAttribute<TListDTO, TViewDTO, TActionTree> : ShiftEntityEndpointAttributeBase
    where TListDTO : ShiftEntityDTOBase
    where TViewDTO : ShiftEntityViewAndUpsertDTO
    where TActionTree : class
{
    public override bool IsSecure => true;
    public override string? ActionName { get; }

    public ShiftEntitySecureEndpointAttribute(string route, string actionName) : base(route)
    {
        ActionName = actionName;
    }
}

/// <summary>
/// Secure CRUD endpoints using a custom repository (<typeparamref name="TRepository"/>).
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class ShiftEntitySecureEndpointAttribute<TListDTO, TViewDTO, TActionTree, TRepository> : ShiftEntityEndpointAttributeBase
    where TListDTO : ShiftEntityDTOBase
    where TViewDTO : ShiftEntityViewAndUpsertDTO
    where TActionTree : class
    where TRepository : ShiftRepositoryBase
{
    public override bool IsSecure => true;
    public override string? ActionName { get; }

    public ShiftEntitySecureEndpointAttribute(string route, string actionName) : base(route)
    {
        ActionName = actionName;
    }
}
