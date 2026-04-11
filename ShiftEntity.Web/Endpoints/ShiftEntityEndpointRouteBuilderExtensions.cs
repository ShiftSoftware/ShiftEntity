using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.AspNetCore.Routing;
using Microsoft.OData.ModelBuilder;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.TypeAuth.AspNetCore.EndpointFilters;
using ShiftSoftware.TypeAuth.Core;
using ShiftSoftware.TypeAuth.Core.Actions;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Web.Endpoints;

/// <summary>
/// Minimal-API entry points that mirror the two controller base classes one-for-one:
/// <list type="bullet">
///   <item><see cref="MapShiftEntityCrud{Repository,Entity,ListDTO,ViewAndUpsertDTO}"/>
///   — counterpart of <c>ShiftEntityControllerAsync</c> (no auth).</item>
///   <item><see cref="MapShiftEntitySecureCrud{Repository,Entity,ListDTO,ViewAndUpsertDTO}"/>
///   — counterpart of <c>ShiftEntitySecureControllerAsync</c>
///   (<c>RequireAuthorization</c> + TypeAuth filter per verb).</item>
/// </list>
/// Both share a single <see cref="ShiftEntityCrudHandler{Repository,Entity,ListDTO,ViewAndUpsertDTO}"/>
/// so there is only one source of truth for CRUD logic.
/// </summary>
public static class ShiftEntityEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Registers the CRUD/revisions/print endpoints with no authentication or
    /// authorization — the minimal-API counterpart of
    /// <see cref="ShiftEntityControllerAsync{Repository,Entity,ListDTO,ViewAndUpsertDTO}"/>.
    /// </summary>
    public static RouteGroupBuilder MapShiftEntityCrud<Repository, Entity, ListDTO, ViewAndUpsertDTO>(
        this IEndpointRouteBuilder endpoints,
        string prefix)
        where Repository : IShiftRepositoryAsync<Entity, ListDTO, ViewAndUpsertDTO>
        where Entity : ShiftEntity<Entity>, new()
        where ViewAndUpsertDTO : ShiftEntityViewAndUpsertDTO
        where ListDTO : ShiftEntityDTOBase
    {
        var group = endpoints.MapGroup(prefix);
        MapCrudEndpointsCore<Repository, Entity, ListDTO, ViewAndUpsertDTO>(group, prefix, secure: false, action: null);
        return group;
    }

    /// <summary>
    /// Registers the CRUD/revisions/print endpoints behind <c>RequireAuthorization</c>
    /// plus a TypeAuth endpoint filter per verb (Read on GET, Write on POST/PUT,
    /// Delete on DELETE) — the minimal-API counterpart of
    /// <see cref="ShiftEntitySecureControllerAsync{Repository,Entity,ListDTO,ViewAndUpsertDTO}"/>.
    ///
    /// Pass <paramref name="action"/> = <c>null</c> to require authentication without
    /// any TypeAuth permission check (matches the secure controller with a null action).
    /// </summary>
    public static RouteGroupBuilder MapShiftEntitySecureCrud<Repository, Entity, ListDTO, ViewAndUpsertDTO>(
        this IEndpointRouteBuilder endpoints,
        string prefix,
        ReadWriteDeleteAction? action)
        where Repository : IShiftRepositoryAsync<Entity, ListDTO, ViewAndUpsertDTO>
        where Entity : ShiftEntity<Entity>, new()
        where ViewAndUpsertDTO : ShiftEntityViewAndUpsertDTO
        where ListDTO : ShiftEntityDTOBase
    {
        var group = endpoints.MapGroup(prefix).RequireAuthorization();
        MapCrudEndpointsCore<Repository, Entity, ListDTO, ViewAndUpsertDTO>(group, prefix, secure: true, action: action);
        return group;
    }

    // ---- Core registration (shared between both variants) ----

    private static void MapCrudEndpointsCore<Repository, Entity, ListDTO, ViewAndUpsertDTO>(
        RouteGroupBuilder group,
        string prefix,
        bool secure,
        ReadWriteDeleteAction? action)
        where Repository : IShiftRepositoryAsync<Entity, ListDTO, ViewAndUpsertDTO>
        where Entity : ShiftEntity<Entity>, new()
        where ViewAndUpsertDTO : ShiftEntityViewAndUpsertDTO
        where ListDTO : ShiftEntityDTOBase
    {
        var handler = new ShiftEntityCrudHandler<Repository, Entity, ListDTO, ViewAndUpsertDTO>();

        // GET {prefix} — list. Empty pattern (not "/") so the route matches the
        // group prefix exactly, with no trailing slash — matches the controller's
        // [HttpGet] on [Route("api/[controller]")] → /api/product.
        var getList = group.MapGet("", async (HttpContext ctx) =>
        {
            var opts = BuildODataQueryOptions<ListDTO>(ctx.Request);
            var data = await handler.GetListAsync(ctx, opts);
            return Results.Ok(data);
        });

        // GET /{key} — single (+ optional asOf for temporal)
        var getSingleRoute = group.MapGet("/{key}", async (HttpContext ctx, string key, DateTimeOffset? asOf) =>
        {
            var (result, _) = await handler.GetSingleAsync(ctx, key, asOf);
            if (result.IsTemporal)
                ctx.Response.Headers.Append(Constants.HttpHeaderVersioning, "Temporal");
            return ToMinimalApiResult(result);
        });

        // GET /{key}/revisions
        var getRevisionsRoute = group.MapGet("/{key}/revisions", async (HttpContext ctx, string key) =>
        {
            var opts = BuildODataQueryOptions<RevisionDTO>(ctx.Request);
            var data = await handler.GetRevisionsAsync(ctx, key, opts);
            return Results.Ok(data);
        });

        // POST {prefix} — create. Empty pattern matches /api/product exactly.
        var postRoute = group.MapPost("", async (HttpContext ctx, ViewAndUpsertDTO dto) =>
        {
            // Validation errors come from the filter (below), which short-circuits
            // before this handler is reached — so pass null here.
            var (result, _) = await handler.PostAsync(ctx, dto, null);

            if (result.CreatedAtKey is not null)
                return Results.Created($"{prefix.TrimEnd('/')}/{result.CreatedAtKey}", result.Body);

            return ToMinimalApiResult(result);
        })
        .AddEndpointFilter<ShiftEntityValidationEndpointFilter>();

        // PUT /{key} — update
        var putRoute = group.MapPut("/{key}", async (HttpContext ctx, string key, ViewAndUpsertDTO dto) =>
        {
            var (result, _) = await handler.PutAsync(ctx, key, dto, null);
            return ToMinimalApiResult(result);
        })
        .AddEndpointFilter<ShiftEntityValidationEndpointFilter>();

        // DELETE /{key}
        var deleteRoute = group.MapDelete("/{key}", async (HttpContext ctx, string key, bool isHardDelete) =>
        {
            var (result, _) = await handler.DeleteAsync(ctx, key, isHardDelete);
            return ToMinimalApiResult(result);
        });

        // GET /print/{key}
        var printRoute = group.MapGet("/print/{key}", async (HttpContext ctx, string key) =>
        {
            var result = await handler.PrintAsync(ctx, key);
            if (result.Stream is not null)
                return Results.Stream(result.Stream, result.ContentType ?? "application/octet-stream");
            return ToMinimalApiResult(result);
        });

        if (secure)
        {
            // Per-verb TypeAuth filter, mirroring ShiftEntitySecureControllerAsync's
            // CanRead / CanWrite / CanDelete wrapping. Passing a null action keeps
            // RequireAuthorization without permission checks (authenticated-only).
            if (action is not null)
            {
                getList.AddEndpointFilter(new TypeAuthEndpointFilter(action, Access.Read));
                getSingleRoute.AddEndpointFilter(new TypeAuthEndpointFilter(action, Access.Read));
                getRevisionsRoute.AddEndpointFilter(new TypeAuthEndpointFilter(action, Access.Read));
                printRoute.AddEndpointFilter(new TypeAuthEndpointFilter(action, Access.Read));
                postRoute.AddEndpointFilter(new TypeAuthEndpointFilter(action, Access.Write));
                putRoute.AddEndpointFilter(new TypeAuthEndpointFilter(action, Access.Write));
                deleteRoute.AddEndpointFilter(new TypeAuthEndpointFilter(action, Access.Delete));
            }
        }
    }

    // ---- Helpers ----

    private static IResult ToMinimalApiResult(CrudResult result)
    {
        if (result.Stream is not null)
            return Results.Stream(result.Stream, result.ContentType ?? "application/octet-stream");

        return Results.Json(result.Body, statusCode: result.StatusCode);
    }

    private static readonly ConcurrentDictionary<Type, ODataQueryContext> _odataContextCache = new();

    /// <summary>
    /// Builds an <see cref="ODataQueryOptions{T}"/> from the current request's query
    /// string. Minimal API has no first-class binder for <see cref="ODataQueryOptions{T}"/>,
    /// so we construct it manually from an EDM model built via
    /// <see cref="ODataConventionModelBuilder"/>. The per-type EDM context is cached
    /// to avoid rebuilding the model on every request.
    /// </summary>
    private static ODataQueryOptions<T> BuildODataQueryOptions<T>(HttpRequest request) where T : class
    {
        var context = _odataContextCache.GetOrAdd(typeof(T), t =>
        {
            var builder = new ODataConventionModelBuilder();
            builder.EntitySet<T>($"{typeof(T).Name}Set");
            var model = builder.GetEdmModel();
            return new ODataQueryContext(model, typeof(T), new());
        });

        return new ODataQueryOptions<T>(context, request);
    }
}
