using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Query;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Web;

public class ShiftEntityControllerBase<Repository, Entity, ListDTO, ViewAndUpsertDTO> : ControllerBase
    where Repository : IShiftRepositoryAsync<Entity, ListDTO, ViewAndUpsertDTO>
    where Entity : ShiftEntity<Entity>, new()
    where ViewAndUpsertDTO : ShiftEntityViewAndUpsertDTO
    where ListDTO : ShiftEntityDTOBase
{
    // Single source of truth for CRUD/revisions/print/selection logic.
    // Every *NonAction method in this file is a thin adapter over the handler
    // that converts CrudResult → ActionResult and ModelState → validation dict.
    private readonly ShiftEntityCrudHandler<Repository, Entity, ListDTO, ViewAndUpsertDTO> _handler = new();

    [NonAction]
    internal Task<ODataDTO<ListDTO>> GetOdataListingNonAction(ODataQueryOptions<ListDTO> oDataQueryOptions, System.Linq.Expressions.Expression<Func<Entity, bool>>? where = null)
        => _handler.GetListAsync(HttpContext, oDataQueryOptions, where);

    [NonAction]
    internal Task<ODataDTO<RevisionDTO>> GetRevisionListingNonAction(string key, ODataQueryOptions<RevisionDTO> oDataQueryOptions)
        => _handler.GetRevisionsAsync(HttpContext, key, oDataQueryOptions);

    [NonAction]
    internal async Task<(ActionResult<ShiftEntityResponse<ViewAndUpsertDTO>> ActionResult, Entity? Entity)> GetSingleNonAction(string key, DateTimeOffset? asOf)
    {
        var (result, entity) = await _handler.GetSingleAsync(HttpContext, key, asOf);

        if (result.IsTemporal)
            Response.Headers.Append(Constants.HttpHeaderVersioning, "Temporal");

        return (ToActionResult(result), entity);
    }

    [NonAction]
    internal async Task<(ActionResult<ShiftEntityResponse<ViewAndUpsertDTO>> ActionResult, Entity? Entity)> PostItemNonAction(ViewAndUpsertDTO dto, string getActionName)
    {
        var (result, entity) = await _handler.PostAsync(HttpContext, dto, BuildValidationErrorsFromModelState());

        if (result.CreatedAtKey is not null)
        {
            return (CreatedAtAction(getActionName, new { key = result.CreatedAtKey }, result.Body), entity);
        }

        return (ToActionResult(result), entity);
    }

    [NonAction]
    internal async Task<(ActionResult<ShiftEntityResponse<ViewAndUpsertDTO>> ActionResult, Entity? Entity)> PutItemNonAction(string key, ViewAndUpsertDTO dto)
    {
        var (result, entity) = await _handler.PutAsync(HttpContext, key, dto, BuildValidationErrorsFromModelState());
        return (ToActionResult(result), entity);
    }

    [NonAction]
    internal async Task<(ActionResult<ShiftEntityResponse<ViewAndUpsertDTO>> ActionResult, Entity? Entity)> DeleteItemNonAction(string key, bool isHardDelete)
    {
        var (result, entity) = await _handler.DeleteAsync(HttpContext, key, isHardDelete);
        return (ToActionResult(result), entity);
    }

    [NonAction]
    internal async Task<ActionResult> PrintNonAction(string key)
    {
        var result = await _handler.PrintAsync(HttpContext, key);

        if (result.Stream is not null)
            return new FileStreamResult(result.Stream, result.ContentType ?? "application/octet-stream");

        return StatusCode(result.StatusCode, result.Body);
    }

    [NonAction]
    internal async Task<ActionResult> PrintTokenNonAction(string key, string urlDescriptor)
    {
        var result = await _handler.PrintTokenAsync(HttpContext, key, urlDescriptor);

        if (result.StatusCode == 200)
            return Ok(result.Body);

        return StatusCode(result.StatusCode, result.Body);
    }

    [NonAction]
    internal bool ValidatePrintSASTokenNonAction(string key, string urlDescriptor, string? expires, string? token)
        => _handler.ValidatePrintSASToken(HttpContext, key, urlDescriptor, expires, token);

    internal Task<List<ListDTO>> GetSelectedListDTOsAsyncBase(
        ODataQueryOptions<ListDTO> oDataQueryOptions,
        bool disableDefaultDataLevelAccess = false,
        bool disableGlobalFilters = false)
        => _handler.GetSelectedListDTOsAsync(HttpContext, oDataQueryOptions, disableDefaultDataLevelAccess, disableGlobalFilters);

    internal Task<List<ListDTO>> GetSelectedListDTOsAsyncBase(
        SelectStateDTO<ListDTO> ids,
        bool disableDefaultDataLevelAccess = false,
        bool disableGlobalFilters = false)
        => _handler.GetSelectedListDTOsAsync(HttpContext, ids, disableDefaultDataLevelAccess, disableGlobalFilters);

    internal Task<List<Entity>> GetSelectedEntitiesAsyncBase(
        ODataQueryOptions<ListDTO> oDataQueryOptions,
        bool disableDefaultDataLevelAccess = false,
        bool disableGlobalFilters = false)
        => _handler.GetSelectedEntitiesAsync(HttpContext, oDataQueryOptions, disableDefaultDataLevelAccess, disableGlobalFilters);

    internal Task<List<Entity>> GetSelectedEntitiesAsyncBase(
        SelectStateDTO<ListDTO> ids,
        bool disableDefaultDataLevelAccess = false,
        bool disableGlobalFilters = false)
        => _handler.GetSelectedEntitiesAsync(HttpContext, ids, disableDefaultDataLevelAccess, disableGlobalFilters);

    // ---- Adapter helpers ----

    private IReadOnlyDictionary<string, string[]>? BuildValidationErrorsFromModelState()
    {
        if (ModelState.IsValid)
            return null;

        return ModelState.ToDictionary(
            x => x.Key,
            x => x.Value is null
                ? Array.Empty<string>()
                : x.Value.Errors.Select(e => e.ErrorMessage).ToArray());
    }

    private ActionResult<ShiftEntityResponse<ViewAndUpsertDTO>> ToActionResult(CrudResult result)
    {
        return result.StatusCode switch
        {
            200 => Ok(result.Body),
            400 => BadRequest(result.Body),
            404 => NotFound(result.Body),
            _ => StatusCode(result.StatusCode, result.Body),
        };
    }
}
