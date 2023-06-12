using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.ShiftEntity.Web.Services;
using ShiftSoftware.TypeAuth.AspNetCore.Services;
using ShiftSoftware.TypeAuth.Core.Actions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Web;

public class ShiftEntitySecureControllerAsync<Repository, Entity, ListDTO, DTO> :
    ShiftEntitySecureControllerAsync<Repository, Entity, ListDTO, DTO, DTO, DTO>
    where Repository : IShiftRepositoryAsync<Entity, ListDTO, DTO>
    where Entity : ShiftEntity<Entity>
    where DTO : ShiftEntityDTO
{
    public ShiftEntitySecureControllerAsync(Repository repository,
        ReadWriteDeleteAction action) :
        base(repository, action)
    {
    }
}

public class ShiftEntitySecureControllerAsync<Repository, Entity, ListDTO, SelectDTO, CreateDTO, UpdateDTO> :
        ShiftEntityControllerAsync<Repository, Entity, ListDTO, SelectDTO, CreateDTO, UpdateDTO>
        where Repository : IShiftRepositoryAsync<Entity, ListDTO, SelectDTO, CreateDTO, UpdateDTO>
        where Entity : ShiftEntity<Entity>
        where UpdateDTO : ShiftEntityDTO
{
    private readonly ReadWriteDeleteAction action;

    public ShiftEntitySecureControllerAsync(
        Repository repository,
        ReadWriteDeleteAction action) : base(repository)
    {
        this.action = action;
    }

    [Authorize]
    public override ActionResult<ODataDTO<IQueryable<ListDTO>>> Get([FromQuery] bool ignoreGlobalFilters = false)
    {
        var typeAuthService = this.HttpContext.RequestServices.GetRequiredService<TypeAuthService>();

        if (!typeAuthService.CanRead(action))
            return Forbid();

        return base.Get(ignoreGlobalFilters);
    }

    [Authorize]
    [HttpGet("{key}")]
    public override async Task<ActionResult<ShiftEntityResponse<SelectDTO>>> GetSingle
        (string key, [FromQuery] DateTime? asOf, [FromQuery] bool ignoreGlobalFilters = false)
    {
        var typeAuthService = this.HttpContext.RequestServices.GetRequiredService<TypeAuthService>();

        if (!typeAuthService.CanRead(action))
            return Forbid();

        return await base.GetSingle(key, asOf, ignoreGlobalFilters);
    }

    [Authorize]
    public override async Task<ActionResult<ODataDTO<List<RevisionDTO>>>> GetRevisions(string key)
    {
        var typeAuthService = this.HttpContext.RequestServices.GetRequiredService<TypeAuthService>();

        if (!typeAuthService.CanRead(action))
            return Forbid();

        return await base.GetRevisions(key);
    }

    [Authorize]
    public override async Task<ActionResult<ShiftEntityResponse<SelectDTO>>> Post([FromBody] CreateDTO dto)
    {
        var typeAuthService = this.HttpContext.RequestServices.GetRequiredService<TypeAuthService>();

        if (!typeAuthService.CanWrite(action))
            return Forbid();

        return await base.Post(dto);
    }

    [Authorize]
    public override async Task<ActionResult<ShiftEntityResponse<SelectDTO>>> Put(string key, [FromBody] UpdateDTO dto)
    {
        var typeAuthService = this.HttpContext.RequestServices.GetRequiredService<TypeAuthService>();

        if (!typeAuthService.CanWrite(action))
            return Forbid();

        return await base.Put(key, dto);
    }

    [Authorize]
    public override async Task<ActionResult<ShiftEntityResponse<SelectDTO>>> Delete(string key)
    {
        var typeAuthService = this.HttpContext.RequestServices.GetRequiredService<TypeAuthService>();

        if (!typeAuthService.CanDelete(action))
            return Forbid();

        return await base.Delete(key);
    }
}
