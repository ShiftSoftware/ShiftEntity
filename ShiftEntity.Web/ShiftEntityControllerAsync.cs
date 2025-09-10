using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Query;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;



namespace ShiftSoftware.ShiftEntity.Web;

public class ShiftEntityControllerAsync<Repository, Entity, ListDTO, ViewAndUpsertDTO> :
    ShiftEntityControllerBase<Repository, Entity, ListDTO, ViewAndUpsertDTO>
    where Repository : IShiftRepositoryAsync<Entity, ListDTO, ViewAndUpsertDTO>
    where Entity : ShiftEntity<Entity>, new()
    where ViewAndUpsertDTO : ShiftEntityViewAndUpsertDTO
    where ListDTO : ShiftEntityDTOBase
{

    public ShiftEntityControllerAsync()
    {

    }

    [HttpGet]
    public virtual async Task<ActionResult<ODataDTO<ListDTO>>> Get(ODataQueryOptions<ListDTO> oDataQueryOptions)
    {
        return Ok(await base.GetOdataListingNonAction(oDataQueryOptions));
    }

    [HttpGet("{key}")]
    public virtual async Task<ActionResult<ShiftEntityResponse<ViewAndUpsertDTO>>> GetSingle(string key, [FromQuery] DateTimeOffset? asOf)
    {
        return (await base.GetSingleNonAction(key, asOf)).ActionResult;
    }

    [HttpGet("{key}/revisions")]
    public virtual async Task<ActionResult<ODataDTO<List<RevisionDTO>>>> GetRevisions(string key, ODataQueryOptions<RevisionDTO> oDataQueryOptions)
    {
        return Ok(await base.GetRevisionListingNonAction(key, oDataQueryOptions));
    }

    [HttpPost]
    public virtual async Task<ActionResult<ShiftEntityResponse<ViewAndUpsertDTO>>> Post([FromBody] ViewAndUpsertDTO dto)
    {
        return (await base.PostItemNonAction(dto)).ActionResult;
    }

    [HttpPut("{key}")]
    public virtual async Task<ActionResult<ShiftEntityResponse<ViewAndUpsertDTO>>> Put(string key, [FromBody] ViewAndUpsertDTO dto)
    {
        return (await base.PutItemNonAction(key, dto)).ActionResult;
    }

    [HttpDelete("{key}")]
    public virtual async Task<ActionResult<ShiftEntityResponse<ViewAndUpsertDTO>>> Delete(string key, [FromQuery] bool isHardDelete = false)
    {
        return (await base.DeleteItemNonAction(key, isHardDelete)).ActionResult;
    }

    [HttpGet("print/{key}")]
    public virtual async Task<ActionResult> Print(string key, [FromQuery] string? expires = null, [FromQuery] string? token = null)
    {
        return (await base.PrintNonAction(key));
    }
}