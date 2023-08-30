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
using ShiftSoftware.TypeAuth.Core.Actions;
using Microsoft.AspNetCore.Authorization;
using ShiftSoftware.TypeAuth.AspNetCore.Services;

namespace ShiftSoftware.ShiftEntity.Web;

public class ShiftEntitySecureControllerAsync<Repository, Entity, ListDTO, SelectDTO, CreateDTO, UpdateDTO> :
    ControllerBase
    where Repository : IShiftRepositoryAsync<Entity, ListDTO, SelectDTO, CreateDTO, UpdateDTO>
    where Entity : ShiftEntity<Entity>
    where UpdateDTO : ShiftEntityDTO
    where ListDTO : ShiftEntityDTOBase
{
    private readonly ReadWriteDeleteAction action;
    private readonly ShiftEntityControllerService<Repository, Entity, ListDTO, SelectDTO, CreateDTO, UpdateDTO> shiftEntityControllerService;
    public ShiftEntitySecureControllerAsync(ReadWriteDeleteAction action)
    {
        this.shiftEntityControllerService = new ShiftEntityControllerService<Repository, Entity, ListDTO, SelectDTO, CreateDTO, UpdateDTO>(this);
        this.action = action;
    }

    [HttpGet]
    [EnableQueryWithHashIdConverter]
    [Authorize]
    public virtual ActionResult<ODataDTO<IQueryable<ListDTO>>> Get(ODataQueryOptions<ListDTO> oDataQueryOptions, [FromQuery] bool showDeletedRows = false)
    {
        var typeAuthService = this.HttpContext.RequestServices.GetRequiredService<TypeAuthService>();

        if (!typeAuthService.CanRead(action))
            return Forbid();

        return Ok(this.shiftEntityControllerService.Get(oDataQueryOptions, showDeletedRows));
    }

    [Authorize]
    [HttpGet("{key}")]
    public virtual async Task<ActionResult<ShiftEntityResponse<SelectDTO>>> GetSingle(string key, [FromQuery] DateTime? asOf)
    {
        var typeAuthService = this.HttpContext.RequestServices.GetRequiredService<TypeAuthService>();

        if (!typeAuthService.CanRead(action))
            return Forbid();

        return (await this.shiftEntityControllerService.GetSingle(key, asOf)).ActionResult;
    }

    [Authorize]
    [HttpGet]
    [EnableQueryWithHashIdConverter]
    public virtual async Task<ActionResult<ODataDTO<List<RevisionDTO>>>> GetRevisions(string key)
    {
        var typeAuthService = this.HttpContext.RequestServices.GetRequiredService<TypeAuthService>();

        if (!typeAuthService.CanRead(action))
            return Forbid();

        return Ok(await this.shiftEntityControllerService.GetRevisions(key));
    }

    [Authorize]
    [HttpPost]
    public virtual async Task<ActionResult<ShiftEntityResponse<SelectDTO>>> Post([FromBody] CreateDTO dto)
    {
        var typeAuthService = this.HttpContext.RequestServices.GetRequiredService<TypeAuthService>();

        if (!typeAuthService.CanWrite(action))
            return Forbid();

        return (await this.shiftEntityControllerService.Post(dto)).ActionResult;
    }

    [Authorize]
    [HttpPut("{key}")]
    public virtual async Task<ActionResult<ShiftEntityResponse<SelectDTO>>> Put(string key, [FromBody] UpdateDTO dto)
    {
        var typeAuthService = this.HttpContext.RequestServices.GetRequiredService<TypeAuthService>();

        if (!typeAuthService.CanWrite(action))
            return Forbid();

        return (await this.shiftEntityControllerService.Put(key, dto)).ActionResult;
    }

    [Authorize]
    [HttpDelete("{key}")]
    public virtual async Task<ActionResult<ShiftEntityResponse<SelectDTO>>> Delete(string key, [FromQuery] bool isHardDelete = false)
    {
        var typeAuthService = this.HttpContext.RequestServices.GetRequiredService<TypeAuthService>();

        if (!typeAuthService.CanDelete(action))
            return Forbid();

        return (await this.shiftEntityControllerService.Delete(key, isHardDelete)).ActionResult;
    }
}

public class ShiftEntitySecureControllerAsync<Repository, Entity, ListDTO, DTO> :
    ShiftEntitySecureControllerAsync<Repository, Entity, ListDTO, DTO, DTO, DTO>
    where Repository : IShiftRepositoryAsync<Entity, ListDTO, DTO>
    where Entity : ShiftEntity<Entity>, new()
    where DTO : ShiftEntityDTO
    where ListDTO : ShiftEntityDTOBase
{
    public ShiftEntitySecureControllerAsync(ReadWriteDeleteAction action) : base(action)
    {
    }
}