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

public class ShiftEntityControllerAsync<Repository, Entity, ListDTO, SelectDTO, CreateDTO, UpdateDTO> :
    ControllerBase
    where Repository : IShiftRepositoryAsync<Entity, ListDTO, SelectDTO, CreateDTO, UpdateDTO>
    where Entity : ShiftEntity<Entity>
    where UpdateDTO : ShiftEntityDTO
    where ListDTO : ShiftEntityDTOBase
{

    private readonly ShiftEntityControllerService<Repository, Entity, ListDTO, SelectDTO, CreateDTO, UpdateDTO> shiftEntityControllerService;
    public ShiftEntityControllerAsync()
    {
        this.shiftEntityControllerService = new ShiftEntityControllerService<Repository, Entity, ListDTO, SelectDTO, CreateDTO, UpdateDTO>(this);

    }

    [HttpGet]
    [EnableQueryWithHashIdConverter]

    public virtual ActionResult<ODataDTO<IQueryable<ListDTO>>> Get(ODataQueryOptions<ListDTO> oDataQueryOptions, [FromQuery] bool showDeletedRows = false)
    {
        return Ok(this.shiftEntityControllerService.Get(oDataQueryOptions, showDeletedRows));
    }

    [HttpGet("{key}")]
    public virtual async Task<ActionResult<ShiftEntityResponse<SelectDTO>>> GetSingle(string key, [FromQuery] DateTime? asOf)
    {
        return (await this.shiftEntityControllerService.GetSingle(key, asOf)).ActionResult;
    }

    [HttpGet]
    [EnableQueryWithHashIdConverter]
    public virtual async Task<ActionResult<ODataDTO<List<RevisionDTO>>>> GetRevisions(string key)
    {
        return Ok(await this.shiftEntityControllerService.GetRevisions(key));
    }

    [HttpPost]
    public virtual async Task<ActionResult<ShiftEntityResponse<SelectDTO>>> Post([FromBody] CreateDTO dto)
    {
        return (await this.shiftEntityControllerService.Post(dto)).ActionResult;
    }

    [HttpPut("{key}")]
    public virtual async Task<ActionResult<ShiftEntityResponse<SelectDTO>>> Put(string key, [FromBody] UpdateDTO dto)
    {
        return (await this.shiftEntityControllerService.Put(key, dto)).ActionResult;
    }

    [HttpDelete("{key}")]
    public virtual async Task<ActionResult<ShiftEntityResponse<SelectDTO>>> Delete(string key, [FromQuery] bool isHardDelete = false)
    {
        return (await this.shiftEntityControllerService.Delete(key, isHardDelete)).ActionResult;
    }

    [NonAction]
    public virtual async Task<List<ListDTO>> GetSelectedItemsAsync(ODataQueryOptions<ListDTO> oDataQueryOptions)
    {
        var repository = HttpContext.RequestServices.GetRequiredService<Repository>();

        var list = repository.OdataList();

        if (oDataQueryOptions.Filter != null)
            list = oDataQueryOptions.Filter.ApplyTo(list, new()) as IQueryable<ListDTO>;

        if (list != null)
            return await list.ToListAsync();

        return new List<ListDTO>();
    }
}

public class ShiftEntityControllerAsync<Repository, Entity, ListDTO, DTO> :
    ShiftEntityControllerAsync<Repository, Entity, ListDTO, DTO, DTO, DTO>
    where Repository : IShiftRepositoryAsync<Entity, ListDTO, DTO>
    where Entity : ShiftEntity<Entity>, new()
    where DTO : ShiftEntityDTO
    where ListDTO : ShiftEntityDTOBase
{
}