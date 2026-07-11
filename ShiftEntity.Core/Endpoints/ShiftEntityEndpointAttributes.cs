using ShiftSoftware.ShiftEntity.Model.Dtos;
using System;

namespace ShiftSoftware.ShiftEntity.Core;

/// <summary>
/// Base for the attribute-driven CRUD endpoint declarations placed on entity classes.
///
/// The framework scans entities for these attributes (<see cref="ShiftEntityEndpointDiscovery"/>)
/// and auto-generates the minimal-API CRUD endpoints + the default AutoMapper map, so an entity
/// needs no controller and — unless it opts into a custom repository or mapper — no extra class either.
///
/// Four families, each with an anonymous and a <c>Secure</c> (RequireAuthorization + per-verb TypeAuth) form:
/// <list type="bullet">
///   <item><c>ShiftEntityEndpoint&lt;TList, TView&gt;</c> — built-in repository + default AutoMapper mapping.</item>
///   <item><c>ShiftEntityEndpoint&lt;TList, TView, TRepository&gt;</c> — a custom repository (used as-is).</item>
///   <item><c>ShiftEntityEndpointWithMapper&lt;TList, TView, TMapper&gt;</c> — built-in repository, but the
///   supplied <c>IShiftEntityMapper&lt;TEntity, TList, TView&gt;</c> replaces AutoMapper.</item>
/// </list>
/// All variants allow multiple, so one entity can expose several endpoints.
///
/// Each concrete attribute exposes its own generic arguments through the <see cref="ListDtoType"/> /
/// <see cref="ViewDtoType"/> / <see cref="ActionTreeType"/> / <see cref="RepositoryType"/> /
/// <see cref="MapperType"/> properties, so discovery never depends on the positional layout of the
/// generic parameters — reordering or adding a type parameter needs no change in the discovery code.
/// </summary>
public abstract class ShiftEntityEndpointAttributeBase : Attribute
{
    /// <summary>The route prefix the CRUD endpoints are mapped under (e.g. <c>"api/country"</c>).</summary>
    public string Route { get; }

    /// <summary>True for the secure variants (RequireAuthorization + per-verb TypeAuth).</summary>
    public abstract bool IsSecure { get; }

    /// <summary>The list DTO type (<c>TListDTO</c>).</summary>
    public abstract Type ListDtoType { get; }

    /// <summary>The view/upsert DTO type (<c>TViewDTO</c>).</summary>
    public abstract Type ViewDtoType { get; }

    /// <summary>The action-tree type (<c>TActionTree</c>) for the secure variants, else null.</summary>
    public virtual Type? ActionTreeType => null;

    /// <summary>
    /// For secure variants: the static field name of the action node on the action tree
    /// (e.g. <c>nameof(StockPlusPlusActionTree.Country)</c>). The named field must be a
    /// <c>ReadWriteDeleteAction</c> declared directly on <c>TActionTree</c> (not a dynamic /
    /// data-level action). Null for anonymous variants.
    /// </summary>
    public virtual string? ActionName => null;

    /// <summary>The custom repository type for the <c>…Endpoint&lt;…, TRepository&gt;</c> variants, else null.</summary>
    public virtual Type? RepositoryType => null;

    /// <summary>The custom mapper type for the <c>…EndpointWithMapper&lt;…, TMapper&gt;</c> variants, else null.</summary>
    public virtual Type? MapperType => null;

    /// <summary>
    /// When true, the built-in repository uses the SOURCE-GENERATED mapper for this endpoint's
    /// (entity, list, view) triple — a <c>[ShiftEntityMapper]</c> partial class must exist for it —
    /// instead of the default AutoMapper mapping. Not valid on the <c>WithMapper</c> variants (the mapper
    /// is already explicit) or the custom-repository variants (a custom repository does its own mapping).
    /// </summary>
    public bool UseGeneratedMapper { get; set; }

    protected ShiftEntityEndpointAttributeBase(string route)
    {
        Route = route;
    }
}

// ---- Anonymous ----

/// <summary>
/// Anonymous CRUD endpoints over the framework's built-in repository (default AutoMapper mapping).
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class ShiftEntityEndpointAttribute<TListDTO, TViewDTO> : ShiftEntityEndpointAttributeBase
    where TListDTO : ShiftEntityDTOBase
    where TViewDTO : ShiftEntityViewAndUpsertDTO
{
    public override bool IsSecure => false;
    public override Type ListDtoType => typeof(TListDTO);
    public override Type ViewDtoType => typeof(TViewDTO);

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
    public override Type ListDtoType => typeof(TListDTO);
    public override Type ViewDtoType => typeof(TViewDTO);
    public override Type? RepositoryType => typeof(TRepository);

    public ShiftEntityEndpointAttribute(string route) : base(route) { }
}

/// <summary>
/// Anonymous CRUD endpoints over the built-in repository, but with AutoMapper replaced by the supplied
/// mapper <typeparamref name="TMapper"/> (an <c>IShiftEntityMapper&lt;TEntity, TListDTO, TViewDTO&gt;</c>).
/// No repository class is needed. Discovery validates that the mapper implements the interface for this
/// entity's exact <c>(entity, list, view)</c> triple.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class ShiftEntityEndpointWithMapperAttribute<TListDTO, TViewDTO, TMapper> : ShiftEntityEndpointAttributeBase
    where TListDTO : ShiftEntityDTOBase
    where TViewDTO : ShiftEntityViewAndUpsertDTO
    where TMapper : class, IShiftEntityMapper
{
    public override bool IsSecure => false;
    public override Type ListDtoType => typeof(TListDTO);
    public override Type ViewDtoType => typeof(TViewDTO);
    public override Type? MapperType => typeof(TMapper);

    public ShiftEntityEndpointWithMapperAttribute(string route) : base(route) { }
}

// ---- Secure ----

/// <summary>
/// Secure CRUD endpoints (RequireAuthorization + per-verb TypeAuth using the
/// <paramref name="actionName"/> node on <typeparamref name="TActionTree"/>) over the built-in
/// repository (default AutoMapper mapping).
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class ShiftEntitySecureEndpointAttribute<TListDTO, TViewDTO, TActionTree> : ShiftEntityEndpointAttributeBase
    where TListDTO : ShiftEntityDTOBase
    where TViewDTO : ShiftEntityViewAndUpsertDTO
    where TActionTree : class
{
    public override bool IsSecure => true;
    public override Type ListDtoType => typeof(TListDTO);
    public override Type ViewDtoType => typeof(TViewDTO);
    public override Type? ActionTreeType => typeof(TActionTree);
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
    public override Type ListDtoType => typeof(TListDTO);
    public override Type ViewDtoType => typeof(TViewDTO);
    public override Type? ActionTreeType => typeof(TActionTree);
    public override string? ActionName { get; }
    public override Type? RepositoryType => typeof(TRepository);

    public ShiftEntitySecureEndpointAttribute(string route, string actionName) : base(route)
    {
        ActionName = actionName;
    }
}

/// <summary>
/// Secure CRUD endpoints over the built-in repository, but with AutoMapper replaced by the supplied
/// mapper <typeparamref name="TMapper"/> (an <c>IShiftEntityMapper&lt;TEntity, TListDTO, TViewDTO&gt;</c>).
/// No repository class is needed. Discovery validates that the mapper implements the interface for this
/// entity's exact <c>(entity, list, view)</c> triple.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class ShiftEntitySecureEndpointWithMapperAttribute<TListDTO, TViewDTO, TActionTree, TMapper> : ShiftEntityEndpointAttributeBase
    where TListDTO : ShiftEntityDTOBase
    where TViewDTO : ShiftEntityViewAndUpsertDTO
    where TActionTree : class
    where TMapper : class, IShiftEntityMapper
{
    public override bool IsSecure => true;
    public override Type ListDtoType => typeof(TListDTO);
    public override Type ViewDtoType => typeof(TViewDTO);
    public override Type? ActionTreeType => typeof(TActionTree);
    public override string? ActionName { get; }
    public override Type? MapperType => typeof(TMapper);

    public ShiftEntitySecureEndpointWithMapperAttribute(string route, string actionName) : base(route)
    {
        ActionName = actionName;
    }
}
