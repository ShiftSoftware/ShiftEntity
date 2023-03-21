﻿using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using ShiftEntityWeb;
using ShiftSoftware.ShiftEntity.Core;
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
    public override IActionResult Get([FromQuery] bool ignoreGlobalFilters = false)
    {
        if (!typeAuthService.CanRead(action))
            return Forbid();

        return base.Get(ignoreGlobalFilters);
    }

    [Authorize]
    [HttpGet("{key}")]
    public override async Task<IActionResult> GetSingle
        (long key, [FromQuery] DateTime? asOf, [FromQuery] bool ignoreGlobalFilters = false)
    {
        if (!typeAuthService.CanRead(action))
            return Forbid();

        if (asOf.HasValue)
            asOf = TimeZoneService.ReadOffsettedDate(asOf.Value);

        return await base.GetSingle(key, asOf, ignoreGlobalFilters);
    }

    [Authorize]
    public override async Task<IActionResult> GetRevisions(long key)
    {
        if (!typeAuthService.CanRead(action))
            return Forbid();

        return await base.GetRevisions(key);
    }

    [Authorize]
    public override async Task<IActionResult> Post([FromBody] CreateDTO dto)
    {
        if (!typeAuthService.CanWrite(action))
            return Forbid();

        return await base.Post(dto);
    }

    [Authorize]
    public override async Task<IActionResult> Put(long key, [FromBody] UpdateDTO dto)
    {
        if (!typeAuthService.CanWrite(action))
            return Forbid();

        return await base.Put(key, dto);
    }

    [Authorize]
    public override async Task<IActionResult> Delete(long key)
    {
        if (!typeAuthService.CanDelete(action))
            return Forbid();

        return await base.Delete(key);
    }
}
