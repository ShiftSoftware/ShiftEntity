using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using ShiftEntityWeb;
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

public class ShiftEntitySecureController<Repository, Entity, ListDTO, DTO> :
    ShiftEntitySecureController<Repository, Entity, ListDTO, DTO, DTO, DTO>
    where Repository : IShiftRepository<Entity, ListDTO, DTO>
    where Entity : ShiftEntity<Entity>
{
    public ShiftEntitySecureController(Repository repository, TypeAuthService typeAuthService, ReadWriteDeleteAction action) :
        base(repository, typeAuthService, action)
    {
    }
}

public class ShiftEntitySecureController<Repository, Entity, ListDTO, SelectDTO, CreateDTO, UpdateDTO> :
        ShiftEntityController<Repository, Entity, ListDTO, SelectDTO, CreateDTO, UpdateDTO>
        where Repository : IShiftRepository<Entity, ListDTO, SelectDTO, CreateDTO, UpdateDTO>
        where Entity : ShiftEntity<Entity>
{
    private readonly TypeAuthService typeAuthService;
    private readonly ReadWriteDeleteAction action;

    public ShiftEntitySecureController(
        Repository repository, 
        TypeAuthService typeAuthService, 
        ReadWriteDeleteAction action) : base(repository)
    {
        this.typeAuthService = typeAuthService;
        this.action = action;
    }

    [Authorize]
    public override ActionResult<ODataDTO<IQueryable<ListDTO>>> Get([FromQuery] bool ignoreGlobalFilters = false)
    {
        if (!typeAuthService.CanRead(action))
            return Forbid();

        return base.Get(ignoreGlobalFilters);
    }

    [Authorize]
    [HttpGet("{key}")]
    public override async Task<ActionResult<ShiftEntityResponse<SelectDTO>>> GetSingle
        (long key, [FromQuery] DateTime? asOf, [FromQuery] bool ignoreGlobalFilters = false)
    {
        if (!typeAuthService.CanRead(action))
            return Forbid();

        return await base.GetSingle(key, asOf, ignoreGlobalFilters);
    }

    [Authorize]
    public override async Task<ActionResult<ODataDTO<List<RevisionDTO>>>> GetRevisions(long key)
    {
        if (!typeAuthService.CanRead(action))
            return Forbid();

        return await base.GetRevisions(key);
    }

    [Authorize]
    public override async Task<ActionResult<ShiftEntityResponse<SelectDTO>>> Post([FromBody] CreateDTO dto)
    {
        if (!typeAuthService.CanWrite(action))
            return Forbid();

        return await base.Post(dto);
    }

    [Authorize]
    public override async Task<ActionResult<ShiftEntityResponse<SelectDTO>>> Put(long key, [FromBody] UpdateDTO dto)
    {
        if (!typeAuthService.CanWrite(action))
            return Forbid();

        return await base.Put(key, dto);
    }

    [Authorize]
    public override async Task<ActionResult<ShiftEntityResponse<SelectDTO>>> Delete(long key)
    {
        if (!typeAuthService.CanDelete(action))
            return Forbid();

        return await base.Delete(key);
    }
}
