using Microsoft.AspNetCore.Routing;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.TypeAuth.Core.Actions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ShiftSoftware.ShiftEntity.Web.Endpoints;

/// <summary>
/// Generates minimal-API CRUD endpoints from entity endpoint attributes (see
/// <see cref="ShiftEntityEndpointDiscovery"/>) by invoking the existing
/// <see cref="ShiftEntityEndpointRouteBuilderExtensions"/> map methods via reflection — no endpoint
/// logic is duplicated.
/// </summary>
internal static class ShiftEntityGeneratedEndpoints
{
    // The only reflection needed: the generic type arguments (entity + DTOs + repository) are runtime
    // Types discovered from the attribute, so the generic Map method can't be called directly — it's
    // closed via MakeGenericMethod against the MapEntityEndpoint helper below, which then calls the real
    // MapShiftEntityCrud / MapShiftEntitySecureCrud extensions directly (compile-time, constraint-checked).
    private static readonly MethodInfo MapEntityEndpointMethod =
        typeof(ShiftEntityGeneratedEndpoints).GetMethod(nameof(MapEntityEndpoint), BindingFlags.NonPublic | BindingFlags.Static)!;

    internal static void Generate(IEndpointRouteBuilder routeBuilder, IEnumerable<ShiftEntityEndpointSpec> specs, Type dbType)
    {
        foreach (var spec in specs)
        {
            var repositoryType = ResolveRepositoryType(spec, dbType);
            ValidateRepository(repositoryType, spec);

            var action = spec.Secure ? ResolveAction(spec.ActionTreeType!, spec.ActionName!) : null;

            MapEntityEndpointMethod
                .MakeGenericMethod(repositoryType, spec.Entity, spec.ListDto, spec.ViewDto)
                .Invoke(null, new object?[] { routeBuilder, spec.Route, spec.Secure, action });
        }
    }

    // Closed over the runtime types by Generate. The MapShiftEntity*Crud calls here are direct: the
    // compiler picks the right (no-configure) overload and checks the generic constraints.
    private static void MapEntityEndpoint<TRepository, TEntity, TListDTO, TViewAndUpsertDTO>(
        IEndpointRouteBuilder endpoints, string route, bool secure, ReadWriteDeleteAction? action)
        where TRepository : IShiftRepositoryAsync<TEntity, TListDTO, TViewAndUpsertDTO>
        where TEntity : ShiftEntity<TEntity>, new()
        where TViewAndUpsertDTO : ShiftEntityViewAndUpsertDTO
        where TListDTO : ShiftEntityDTOBase
    {
        if (secure)
            endpoints.MapShiftEntitySecureCrud<TRepository, TEntity, TListDTO, TViewAndUpsertDTO>(route, action);
        else
            endpoints.MapShiftEntityCrud<TRepository, TEntity, TListDTO, TViewAndUpsertDTO>(route);
    }

    // Custom repository if the attribute named one; otherwise the framework's built-in
    // ShiftRepository<DB, Entity, ListDTO, ViewDTO> closed over the app's concrete DB.
    internal static Type ResolveRepositoryType(ShiftEntityEndpointSpec spec, Type dbType)
        => spec.Repository ?? typeof(ShiftRepository<,,,>).MakeGenericType(dbType, spec.Entity, spec.ListDto, spec.ViewDto);

    // The map methods require Repository : IShiftRepositoryAsync<Entity, ListDTO, ViewDTO>; the attribute
    // only constrains TRepository : ShiftRepositoryBase. Validate here so a mismatched custom repository
    // fails with a clear message instead of an opaque MakeGenericMethod ArgumentException.
    private static void ValidateRepository(Type repositoryType, ShiftEntityEndpointSpec spec)
    {
        var required = typeof(IShiftRepositoryAsync<,,>).MakeGenericType(spec.Entity, spec.ListDto, spec.ViewDto);

        if (!required.IsAssignableFrom(repositoryType))
            throw new InvalidOperationException(
                $"Repository '{repositoryType.FullName}' for the endpoint '{spec.Route}' on entity '{spec.Entity.FullName}' " +
                $"must implement {required.FullName}. A custom repository must be a " +
                $"ShiftRepository<DB, {spec.Entity.Name}, {spec.ListDto.Name}, {spec.ViewDto.Name}> (or a subclass).");
    }

    private static ReadWriteDeleteAction ResolveAction(Type actionTreeType, string actionName)
    {
        var field = actionTreeType
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(f => string.Equals(f.Name, actionName, StringComparison.OrdinalIgnoreCase));

        if (field is null)
            throw new InvalidOperationException(
                $"Action '{actionName}' was not found as a public static field on '{actionTreeType.FullName}'. " +
                "The TActionTree of a secure endpoint attribute must directly declare the named action field.");

        if (field.GetValue(null) is not ReadWriteDeleteAction action)
            throw new InvalidOperationException(
                $"Action '{actionName}' on '{actionTreeType.FullName}' is not a {nameof(ReadWriteDeleteAction)}. " +
                "Secure endpoints can only bind to a ReadWriteDeleteAction node (not a dynamic / data-level action).");

        return action;
    }
}
