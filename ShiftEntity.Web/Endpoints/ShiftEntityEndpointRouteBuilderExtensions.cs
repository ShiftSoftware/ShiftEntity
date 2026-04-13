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
///   <item><see cref="MapShiftEntityCrud{Repository,Entity,ListDTO,ViewAndUpsertDTO}(IEndpointRouteBuilder,string)"/>
///   — counterpart of <c>ShiftEntityControllerAsync</c> (no auth).</item>
///   <item><see cref="MapShiftEntitySecureCrud{Repository,Entity,ListDTO,ViewAndUpsertDTO}(IEndpointRouteBuilder,string,ReadWriteDeleteAction?)"/>
///   — counterpart of <c>ShiftEntitySecureControllerAsync</c>
///   (<c>RequireAuthorization</c> + TypeAuth filter per verb).</item>
/// </list>
/// Both share a single <see cref="ShiftEntityCrudHandler{Repository,Entity,ListDTO,ViewAndUpsertDTO}"/>
/// so there is only one source of truth for CRUD logic.
///
/// Each method has an overload accepting
/// <see cref="Action{ShiftEntityEndpointConfig}"/> for overriding individual endpoint
/// handlers — the minimal-API equivalent of overriding virtual methods in a controller.
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
        MapCrudEndpointsCore<Repository, Entity, ListDTO, ViewAndUpsertDTO>(group, prefix, secure: false, action: null, config: null);
        return group;
    }

    /// <summary>
    /// Registers the CRUD/revisions/print endpoints with no authentication or
    /// authorization, with per-endpoint override support.
    /// </summary>
    public static RouteGroupBuilder MapShiftEntityCrud<Repository, Entity, ListDTO, ViewAndUpsertDTO>(
        this IEndpointRouteBuilder endpoints,
        string prefix,
        Action<ShiftEntityEndpointConfig<Repository, Entity, ListDTO, ViewAndUpsertDTO>> configure)
        where Repository : IShiftRepositoryAsync<Entity, ListDTO, ViewAndUpsertDTO>
        where Entity : ShiftEntity<Entity>, new()
        where ViewAndUpsertDTO : ShiftEntityViewAndUpsertDTO
        where ListDTO : ShiftEntityDTOBase
    {
        var config = new ShiftEntityEndpointConfig<Repository, Entity, ListDTO, ViewAndUpsertDTO>();
        configure(config);
        var group = endpoints.MapGroup(prefix);
        MapCrudEndpointsCore<Repository, Entity, ListDTO, ViewAndUpsertDTO>(group, prefix, secure: false, action: null, config: config);
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
        MapCrudEndpointsCore<Repository, Entity, ListDTO, ViewAndUpsertDTO>(group, prefix, secure: true, action: action, config: null);
        return group;
    }

    /// <summary>
    /// Registers the CRUD/revisions/print endpoints behind <c>RequireAuthorization</c>
    /// plus a TypeAuth endpoint filter per verb, with per-endpoint override support.
    ///
    /// Pass <paramref name="action"/> = <c>null</c> to require authentication without
    /// any TypeAuth permission check.
    /// </summary>
    public static RouteGroupBuilder MapShiftEntitySecureCrud<Repository, Entity, ListDTO, ViewAndUpsertDTO>(
        this IEndpointRouteBuilder endpoints,
        string prefix,
        ReadWriteDeleteAction? action,
        Action<ShiftEntityEndpointConfig<Repository, Entity, ListDTO, ViewAndUpsertDTO>> configure)
        where Repository : IShiftRepositoryAsync<Entity, ListDTO, ViewAndUpsertDTO>
        where Entity : ShiftEntity<Entity>, new()
        where ViewAndUpsertDTO : ShiftEntityViewAndUpsertDTO
        where ListDTO : ShiftEntityDTOBase
    {
        var config = new ShiftEntityEndpointConfig<Repository, Entity, ListDTO, ViewAndUpsertDTO>();
        configure(config);
        var group = endpoints.MapGroup(prefix).RequireAuthorization();
        MapCrudEndpointsCore<Repository, Entity, ListDTO, ViewAndUpsertDTO>(group, prefix, secure: true, action: action, config: config);
        return group;
    }

    // ---- Core registration (shared between all variants) ----

    private static void MapCrudEndpointsCore<Repository, Entity, ListDTO, ViewAndUpsertDTO>(
        RouteGroupBuilder group,
        string prefix,
        bool secure,
        ReadWriteDeleteAction? action,
        ShiftEntityEndpointConfig<Repository, Entity, ListDTO, ViewAndUpsertDTO>? config)
        where Repository : IShiftRepositoryAsync<Entity, ListDTO, ViewAndUpsertDTO>
        where Entity : ShiftEntity<Entity>, new()
        where ViewAndUpsertDTO : ShiftEntityViewAndUpsertDTO
        where ListDTO : ShiftEntityDTOBase
    {
        var handler = new ShiftEntityCrudHandler<Repository, Entity, ListDTO, ViewAndUpsertDTO>();

        // ---- Default handlers ----

        Func<HttpContext, Task<IResult>> defaultGetList = async (HttpContext ctx) =>
        {
            var opts = BuildODataQueryOptions<ListDTO>(ctx.Request);
            var data = await handler.GetListAsync(ctx, opts);
            return Results.Ok(data);
        };

        Func<HttpContext, string, DateTimeOffset?, Task<IResult>> defaultGetSingle = async (HttpContext ctx, string key, DateTimeOffset? asOf) =>
        {
            var (result, _) = await handler.GetSingleAsync(ctx, key, asOf);
            if (result.IsTemporal)
                ctx.Response.Headers.Append(Constants.HttpHeaderVersioning, "Temporal");
            return ToMinimalApiResult(result);
        };

        Func<HttpContext, string, Task<IResult>> defaultGetRevisions = async (HttpContext ctx, string key) =>
        {
            var opts = BuildODataQueryOptions<RevisionDTO>(ctx.Request);
            var data = await handler.GetRevisionsAsync(ctx, key, opts);
            return Results.Ok(data);
        };

        Func<HttpContext, ViewAndUpsertDTO, Task<IResult>> defaultPost = async (HttpContext ctx, ViewAndUpsertDTO dto) =>
        {
            var (result, _) = await handler.PostAsync(ctx, dto, null);
            if (result.CreatedAtKey is not null)
                return Results.Created($"{prefix.TrimEnd('/')}/{result.CreatedAtKey}", result.Body);
            return ToMinimalApiResult(result);
        };

        Func<HttpContext, string, ViewAndUpsertDTO, Task<IResult>> defaultPut = async (HttpContext ctx, string key, ViewAndUpsertDTO dto) =>
        {
            var (result, _) = await handler.PutAsync(ctx, key, dto, null);
            return ToMinimalApiResult(result);
        };

        Func<HttpContext, string, bool, Task<IResult>> defaultDelete = async (HttpContext ctx, string key, bool isHardDelete) =>
        {
            var (result, _) = await handler.DeleteAsync(ctx, key, isHardDelete);
            return ToMinimalApiResult(result);
        };

        Func<HttpContext, string, Task<IResult>> defaultPrint = async (HttpContext ctx, string key) =>
        {
            var result = await handler.PrintAsync(ctx, key);
            if (result.Stream is not null)
                return Results.Stream(result.Stream, result.ContentType ?? "application/octet-stream");
            return ToMinimalApiResult(result);
        };

        // ---- Register endpoints (override-aware) ----

        // GET {prefix} — list
        var getList = group.MapGet("", async (HttpContext ctx) =>
            config?._getListOverride is not null
                ? await config._getListOverride(defaultGetList, ctx)
                : await defaultGetList(ctx));

        // GET /{key} — single (+ optional asOf for temporal)
        var getSingleRoute = group.MapGet("/{key}", async (HttpContext ctx, string key, DateTimeOffset? asOf) =>
            config?._getSingleOverride is not null
                ? await config._getSingleOverride(defaultGetSingle, ctx, key, asOf)
                : await defaultGetSingle(ctx, key, asOf));

        // GET /{key}/revisions
        var getRevisionsRoute = group.MapGet("/{key}/revisions", async (HttpContext ctx, string key) =>
            config?._getRevisionsOverride is not null
                ? await config._getRevisionsOverride(defaultGetRevisions, ctx, key)
                : await defaultGetRevisions(ctx, key));

        // POST {prefix} — create
        var postRoute = group.MapPost("", async (HttpContext ctx, ViewAndUpsertDTO dto) =>
            config?._postOverride is not null
                ? await config._postOverride(defaultPost, ctx, dto)
                : await defaultPost(ctx, dto))
            .AddEndpointFilter<ShiftEntityValidationEndpointFilter>();

        // PUT /{key} — update
        var putRoute = group.MapPut("/{key}", async (HttpContext ctx, string key, ViewAndUpsertDTO dto) =>
            config?._putOverride is not null
                ? await config._putOverride(defaultPut, ctx, key, dto)
                : await defaultPut(ctx, key, dto))
            .AddEndpointFilter<ShiftEntityValidationEndpointFilter>();

        // DELETE /{key}
        var deleteRoute = group.MapDelete("/{key}", async (HttpContext ctx, string key, bool isHardDelete) =>
            config?._deleteOverride is not null
                ? await config._deleteOverride(defaultDelete, ctx, key, isHardDelete)
                : await defaultDelete(ctx, key, isHardDelete));

        // GET /print/{key}
        var printRoute = group.MapGet("/print/{key}", async (HttpContext ctx, string key) =>
            config?._printOverride is not null
                ? await config._printOverride(defaultPrint, ctx, key)
                : await defaultPrint(ctx, key));

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
    internal static ODataQueryOptions<T> BuildODataQueryOptions<T>(HttpRequest request) where T : class
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
