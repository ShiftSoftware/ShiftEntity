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
using ShiftSoftware.ShiftEntity.Web.Extensions;
using ShiftSoftware.ShiftIdentity.Core.DTOs.Region;
using ShiftSoftware.ShiftIdentity.Core.DTOs.Company;
using ShiftSoftware.ShiftIdentity.Core.DTOs.CompanyBranch;

namespace ShiftSoftware.ShiftEntity.Web;

public class ShiftEntitySecureControllerAsync<Repository, Entity, ListDTO, SelectDTO, CreateDTO, UpdateDTO> :
    ShiftEntityControllerBase<Repository, Entity, ListDTO, SelectDTO, CreateDTO, UpdateDTO>
    where Repository : IShiftRepositoryAsync<Entity, ListDTO, SelectDTO, CreateDTO, UpdateDTO>
    where Entity : ShiftEntity<Entity>
    where UpdateDTO : ShiftEntityDTO
    where ListDTO : ShiftEntityDTOBase
{
    private readonly ReadWriteDeleteAction action;
    public ShiftEntitySecureControllerAsync(ReadWriteDeleteAction action)
    {
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

        System.Linq.Expressions.Expression<Func<Entity, bool>> where = x => true;

        List<long?>? accessibleRegions = null;   //Read from TypeAuth
        List<long?>? accessibleCompanies = null; //Read from TypeAuth
        List<long?>? accessibleBranches = null;  //Read from TypeAuth

        where = x =>
            ((accessibleRegions == null || x.RegionID == null) ? true : accessibleRegions.Contains(x.RegionID)) &&
            ((accessibleCompanies == null || x.CompanyID == null) ? true : accessibleCompanies.Contains(x.CompanyID)) &&
            ((accessibleBranches == null || x.CompanyBranchID == null) ? true : accessibleBranches.Contains(x.CompanyBranchID));

        return Ok(base.GetOdataListing(oDataQueryOptions, showDeletedRows, where));
    }

    private bool HasDefaultDataLevelAccess(TypeAuthService typeAuthService, Entity? entity, TypeAuth.Core.Access access)
    {
        if (entity?.RegionID is not null)
        {
            if (!typeAuthService.Can(ShiftIdentity.Core.ShiftIdentityActions.DataLevelAccess.Regions, access, ShiftEntityHashIds.Encode<RegionDTO>(entity.RegionID.Value), this.HttpContext.GetHashedRegionID()))
                return false;
        }

        if (entity?.CompanyID is not null)
        {
            if (!typeAuthService.Can(ShiftIdentity.Core.ShiftIdentityActions.DataLevelAccess.Companies, access, ShiftEntityHashIds.Encode<CompanyDTO>(entity.CompanyID.Value), this.HttpContext.GetHashedCompanyID()))
                return false;
        }

        if (entity?.CompanyBranchID is not null)
        {
            if (!typeAuthService.Can(ShiftIdentity.Core.ShiftIdentityActions.DataLevelAccess.Branches, access, ShiftEntityHashIds.Encode<CompanyBranchDTO>(entity.CompanyBranchID.Value), this.HttpContext.GetHashedCompanyBranchID()))
                return false;
        }

        return true;
    }

    [Authorize]
    [HttpGet("{key}")]
    public virtual async Task<ActionResult<ShiftEntityResponse<SelectDTO>>> GetSingle(string key, [FromQuery] DateTime? asOf)
    {
        var typeAuthService = this.HttpContext.RequestServices.GetRequiredService<TypeAuthService>();

        if (!typeAuthService.CanRead(action))
            return Forbid();

        var result = (await base.GetSingleItem(key, asOf));

        if (!HasDefaultDataLevelAccess(typeAuthService, result.Entity, TypeAuth.Core.Access.Read))
            return Forbid();

        return result.ActionResult;
    }

    [Authorize]
    [HttpGet]
    [EnableQueryWithHashIdConverter]
    public virtual async Task<ActionResult<ODataDTO<List<RevisionDTO>>>> GetRevisions(string key)
    {
        var typeAuthService = this.HttpContext.RequestServices.GetRequiredService<TypeAuthService>();

        if (!typeAuthService.CanRead(action))
            return Forbid();

        return Ok(await base.GetRevisionListing(key));
    }

    [Authorize]
    [HttpPost]
    public virtual async Task<ActionResult<ShiftEntityResponse<SelectDTO>>> Post([FromBody] CreateDTO dto)
    {
        var typeAuthService = this.HttpContext.RequestServices.GetRequiredService<TypeAuthService>();

        if (!typeAuthService.CanWrite(action))
            return Forbid();

        var result = await base.PostItem(dto);

        if (!HasDefaultDataLevelAccess(typeAuthService, result.Entity, TypeAuth.Core.Access.Write))
            return Forbid();

        return result.ActionResult;
    }

    [Authorize]
    [HttpPut("{key}")]
    public virtual async Task<ActionResult<ShiftEntityResponse<SelectDTO>>> Put(string key, [FromBody] UpdateDTO dto)
    {
        var typeAuthService = this.HttpContext.RequestServices.GetRequiredService<TypeAuthService>();

        if (!typeAuthService.CanWrite(action))
            return Forbid();

        var result = await base.PutItem(key, dto);

        if (!HasDefaultDataLevelAccess(typeAuthService, result.Entity, TypeAuth.Core.Access.Write))
            return Forbid();

        return result.ActionResult;
    }

    [Authorize]
    [HttpDelete("{key}")]
    public virtual async Task<ActionResult<ShiftEntityResponse<SelectDTO>>> Delete(string key, [FromQuery] bool isHardDelete = false)
    {
        var typeAuthService = this.HttpContext.RequestServices.GetRequiredService<TypeAuthService>();

        if (!typeAuthService.CanDelete(action))
            return Forbid();

        var result = await base.DeleteItem(key, isHardDelete);

        if (!HasDefaultDataLevelAccess(typeAuthService, result.Entity, TypeAuth.Core.Access.Delete))
            return Forbid();

        return result.ActionResult;
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