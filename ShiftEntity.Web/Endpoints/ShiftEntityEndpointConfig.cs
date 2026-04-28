using Microsoft.AspNetCore.Http;
using System;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Web.Endpoints;

/// <summary>
/// Configuration object for overriding individual CRUD endpoint handlers registered
/// by <see cref="ShiftEntityEndpointRouteBuilderExtensions.MapShiftEntityCrud{Repository,Entity,ListDTO,ViewAndUpsertDTO}"/>
/// and its secure counterpart.
///
/// Each <c>OverrideX</c> method accepts a delegate whose first parameter is the
/// <b>original</b> (default) handler — calling it is equivalent to <c>base.X()</c>
/// in a controller override.
///
/// <example>
/// <code>
/// endpoints.MapShiftEntitySecureCrud&lt;MyRepo, MyEntity, MyListDTO, MyDTO&gt;(
///     "api/my-entity", action, crud =&gt;
///     {
///         crud.OverridePost(async (original, ctx, dto) =&gt;
///         {
///             // custom before logic
///             var result = await original(ctx, dto);   // call the default handler
///             // custom after logic
///             return result;
///         });
///     });
/// </code>
/// </example>
/// </summary>
public class ShiftEntityEndpointConfig<Repository, Entity, ListDTO, ViewAndUpsertDTO>
{
    internal Func<Func<HttpContext, Task<IResult>>,
        HttpContext, Task<IResult>>? _getListOverride;

    internal Func<Func<HttpContext, string, DateTimeOffset?, Task<IResult>>,
        HttpContext, string, DateTimeOffset?, Task<IResult>>? _getSingleOverride;

    internal Func<Func<HttpContext, string, Task<IResult>>,
        HttpContext, string, Task<IResult>>? _getRevisionsOverride;

    internal Func<Func<HttpContext, ViewAndUpsertDTO, Task<IResult>>,
        HttpContext, ViewAndUpsertDTO, Task<IResult>>? _postOverride;

    internal Func<Func<HttpContext, string, ViewAndUpsertDTO, Task<IResult>>,
        HttpContext, string, ViewAndUpsertDTO, Task<IResult>>? _putOverride;

    internal Func<Func<HttpContext, string, bool, Task<IResult>>,
        HttpContext, string, bool, Task<IResult>>? _deleteOverride;

    internal Func<Func<HttpContext, string, Task<IResult>>,
        HttpContext, string, Task<IResult>>? _printOverride;

    /// <summary>
    /// Override the GET list endpoint. The <paramref name="handler"/> receives the
    /// original (default) handler as its first parameter.
    /// </summary>
    public ShiftEntityEndpointConfig<Repository, Entity, ListDTO, ViewAndUpsertDTO>
        OverrideGetList(
            Func<Func<HttpContext, Task<IResult>>,
                HttpContext, Task<IResult>> handler)
    {
        _getListOverride = handler;
        return this;
    }

    /// <summary>
    /// Override the GET single endpoint. The <paramref name="handler"/> receives the
    /// original (default) handler as its first parameter.
    /// </summary>
    public ShiftEntityEndpointConfig<Repository, Entity, ListDTO, ViewAndUpsertDTO>
        OverrideGetSingle(
            Func<Func<HttpContext, string, DateTimeOffset?, Task<IResult>>,
                HttpContext, string, DateTimeOffset?, Task<IResult>> handler)
    {
        _getSingleOverride = handler;
        return this;
    }

    /// <summary>
    /// Override the GET revisions endpoint. The <paramref name="handler"/> receives the
    /// original (default) handler as its first parameter.
    /// </summary>
    public ShiftEntityEndpointConfig<Repository, Entity, ListDTO, ViewAndUpsertDTO>
        OverrideGetRevisions(
            Func<Func<HttpContext, string, Task<IResult>>,
                HttpContext, string, Task<IResult>> handler)
    {
        _getRevisionsOverride = handler;
        return this;
    }

    /// <summary>
    /// Override the POST (create) endpoint. The <paramref name="handler"/> receives the
    /// original (default) handler as its first parameter.
    /// </summary>
    public ShiftEntityEndpointConfig<Repository, Entity, ListDTO, ViewAndUpsertDTO>
        OverridePost(
            Func<Func<HttpContext, ViewAndUpsertDTO, Task<IResult>>,
                HttpContext, ViewAndUpsertDTO, Task<IResult>> handler)
    {
        _postOverride = handler;
        return this;
    }

    /// <summary>
    /// Override the PUT (update) endpoint. The <paramref name="handler"/> receives the
    /// original (default) handler as its first parameter.
    /// </summary>
    public ShiftEntityEndpointConfig<Repository, Entity, ListDTO, ViewAndUpsertDTO>
        OverridePut(
            Func<Func<HttpContext, string, ViewAndUpsertDTO, Task<IResult>>,
                HttpContext, string, ViewAndUpsertDTO, Task<IResult>> handler)
    {
        _putOverride = handler;
        return this;
    }

    /// <summary>
    /// Override the DELETE endpoint. The <paramref name="handler"/> receives the
    /// original (default) handler as its first parameter.
    /// </summary>
    public ShiftEntityEndpointConfig<Repository, Entity, ListDTO, ViewAndUpsertDTO>
        OverrideDelete(
            Func<Func<HttpContext, string, bool, Task<IResult>>,
                HttpContext, string, bool, Task<IResult>> handler)
    {
        _deleteOverride = handler;
        return this;
    }

    /// <summary>
    /// Override the GET print endpoint. The <paramref name="handler"/> receives the
    /// original (default) handler as its first parameter.
    /// </summary>
    public ShiftEntityEndpointConfig<Repository, Entity, ListDTO, ViewAndUpsertDTO>
        OverridePrint(
            Func<Func<HttpContext, string, Task<IResult>>,
                HttpContext, string, Task<IResult>> handler)
    {
        _printOverride = handler;
        return this;
    }
}
