using System.IO;

namespace ShiftSoftware.ShiftEntity.Web;

/// <summary>
/// Framework-agnostic result from <see cref="ShiftEntityCrudHandler{Repository, Entity, ListDTO, ViewAndUpsertDTO}"/>.
/// Controllers convert this to <c>ActionResult</c>, minimal API endpoints convert to <c>IResult</c>.
/// Contains no MVC-specific types — no <c>ActionResult</c>, no <c>IActionResult</c>, no <c>HttpContext</c>.
/// </summary>
public class CrudResult
{
    /// <summary>HTTP status code (200, 201, 400, 404, 409, etc.).</summary>
    public int StatusCode { get; set; }

    /// <summary>The response body to serialize (usually a <see cref="ShiftSoftware.ShiftEntity.Model.ShiftEntityResponse{T}"/>).</summary>
    public object? Body { get; set; }

    /// <summary>
    /// When set, the result was a successful POST. The adapter should emit a 201 Created
    /// with a Location header pointing at the GetSingle route using this encoded key.
    /// </summary>
    public string? CreatedAtKey { get; set; }

    /// <summary>
    /// When true, the result is a GetSingle on a temporal entity. The adapter should append
    /// the <c>Versioning: Temporal</c> header to the response.
    /// </summary>
    public bool IsTemporal { get; set; }

    /// <summary>
    /// Non-null only for the Print endpoint — a PDF stream to be streamed as the response body.
    /// When set, <see cref="Body"/> is ignored.
    /// </summary>
    public Stream? Stream { get; set; }

    /// <summary>Content type for the stream (typically <c>application/pdf</c>).</summary>
    public string? ContentType { get; set; }

    public static CrudResult Ok(object? body) => new() { StatusCode = 200, Body = body };

    public static CrudResult Ok(object? body, bool isTemporal) =>
        new() { StatusCode = 200, Body = body, IsTemporal = isTemporal };

    public static CrudResult Created(object? body, string createdAtKey) =>
        new() { StatusCode = 201, Body = body, CreatedAtKey = createdAtKey };

    public static CrudResult BadRequest(object? body) => new() { StatusCode = 400, Body = body };

    public static CrudResult NotFound(object? body) => new() { StatusCode = 404, Body = body };

    public static CrudResult Status(int statusCode, object? body) =>
        new() { StatusCode = statusCode, Body = body };

    public static CrudResult File(Stream stream, string contentType) =>
        new() { StatusCode = 200, Stream = stream, ContentType = contentType };
}
