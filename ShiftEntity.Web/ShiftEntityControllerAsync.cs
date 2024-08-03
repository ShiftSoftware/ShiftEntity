using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.EntityFrameworkCore;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.ShiftEntity.Web.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OData;



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
    [EnableQueryWithHashIdConverter]

    public virtual ActionResult<ODataDTO<IQueryable<ListDTO>>> Get(ODataQueryOptions<ListDTO> oDataQueryOptions)
    {
        return Ok(base.GetOdataListing(oDataQueryOptions));
    }

    [HttpGet("{key}")]
    public virtual async Task<ActionResult<ShiftEntityResponse<ViewAndUpsertDTO>>> GetSingle(string key, [FromQuery] DateTimeOffset? asOf)
    {
        return (await base.GetSingle(key, asOf, null)).ActionResult;
    }

    [HttpGet]
    [EnableQueryWithHashIdConverter]
    public virtual async Task<ActionResult<ODataDTO<List<RevisionDTO>>>> GetRevisions(string key)
    {
        return Ok(await base.GetRevisionListing(key));
    }

    [HttpPost]
    public virtual async Task<ActionResult<ShiftEntityResponse<ViewAndUpsertDTO>>> Post([FromBody] ViewAndUpsertDTO dto)
    {
        return (await base.PostItem(dto, null)).ActionResult;
    }

    [HttpPut("{key}")]
    public virtual async Task<ActionResult<ShiftEntityResponse<ViewAndUpsertDTO>>> Put(string key, [FromBody] ViewAndUpsertDTO dto)
    {
        return (await base.PutItem(key, dto, null)).ActionResult;
    }

    [HttpDelete("{key}")]
    public virtual async Task<ActionResult<ShiftEntityResponse<ViewAndUpsertDTO>>> Delete(string key, [FromQuery] bool isHardDelete = false)
    {
        return (await base.DeleteItem(key, isHardDelete, null)).ActionResult;
    }

    [HttpGet("print/{key}")]
    public virtual async Task<ActionResult> Print(string key, [FromQuery] string? expires = null, [FromQuery] string? token = null)
    {
        return (await base.Print(key));
    }
}
