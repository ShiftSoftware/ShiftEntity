using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Query;
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
using System.Linq.Expressions;
using Microsoft.AspNetCore.Http;
using ShiftSoftware.TypeAuth.Core;

namespace ShiftSoftware.ShiftEntity.Web;

public class ShiftEntitySecureControllerAsync<Repository, Entity, ListDTO, SelectDTO, CreateDTO, UpdateDTO> :
    ShiftEntityControllerBase<Repository, Entity, ListDTO, SelectDTO, CreateDTO, UpdateDTO>
    where Repository : IShiftRepositoryAsync<Entity, ListDTO, SelectDTO, CreateDTO, UpdateDTO>
    where Entity : ShiftEntity<Entity>
    where UpdateDTO : ShiftEntityDTO
    where ListDTO : ShiftEntityDTOBase
{
    private readonly ReadWriteDeleteAction action;

    private readonly DynamicActionFilterOptions<Entity>? dynamicActionFilterOptions;

    public ShiftEntitySecureControllerAsync(ReadWriteDeleteAction action, Action<DynamicActionFilterOptions<Entity>>? dynamicActionFilterOptions = null)
    {
        this.action = action;

        if (dynamicActionFilterOptions is not null)
        {
            this.dynamicActionFilterOptions = new();

            dynamicActionFilterOptions.Invoke(this.dynamicActionFilterOptions);
        }
    }

    [HttpGet]
    [EnableQueryWithHashIdConverter]
    [Authorize]
    public virtual ActionResult<ODataDTO<IQueryable<ListDTO>>> Get(ODataQueryOptions<ListDTO> oDataQueryOptions, [FromQuery] bool showDeletedRows = false)
    {
        var typeAuthService = this.HttpContext.RequestServices.GetRequiredService<TypeAuthService>();

        if (!typeAuthService.CanRead(action))
            return Forbid();

        var accessibleRegionsTypeAuth = typeAuthService.GetAccessibleItems(ShiftIdentity.Core.ShiftIdentityActions.DataLevelAccess.Regions, x => x == TypeAuth.Core.Access.Read, this.HttpContext.GetHashedRegionID());
        var accessibleCompaniesTypeAuth = typeAuthService.GetAccessibleItems(ShiftIdentity.Core.ShiftIdentityActions.DataLevelAccess.Companies, x => x == TypeAuth.Core.Access.Read, this.HttpContext.GetHashedCompanyID());
        var accessibleBranchesTypeAuth = typeAuthService.GetAccessibleItems(ShiftIdentity.Core.ShiftIdentityActions.DataLevelAccess.Branches, x => x == TypeAuth.Core.Access.Read, this.HttpContext.GetHashedCompanyBranchID());

        List<long?>? accessibleRegions = accessibleRegionsTypeAuth.WildCard ? null : accessibleRegionsTypeAuth.AccessibleIds.Select(x => (long?)ShiftEntityHashIds.Decode<RegionDTO>(x)).ToList();
        List<long?>? accessibleCompanies = accessibleCompaniesTypeAuth.WildCard ? null : accessibleCompaniesTypeAuth.AccessibleIds.Select(x => (long?)ShiftEntityHashIds.Decode<CompanyDTO>(x)).ToList();
        List<long?>? accessibleBranches = accessibleBranchesTypeAuth.WildCard ? null : accessibleBranchesTypeAuth.AccessibleIds.Select(x => (long?)ShiftEntityHashIds.Decode<CompanyBranchDTO>(x)).ToList();

        Expression<Func<Entity, bool>> companyWhere = x =>
            ((accessibleRegions == null || x.RegionID == null) ? true : accessibleRegions.Contains(x.RegionID)) &&
            ((accessibleCompanies == null || x.CompanyID == null) ? true : accessibleCompanies.Contains(x.CompanyID)) &&
            ((accessibleBranches == null || x.CompanyBranchID == null) ? true : accessibleBranches.Contains(x.CompanyBranchID));

        var dynamicActionWhere = GetDynamicActionExpression(typeAuthService, Access.Read);

        var finalWhere = dynamicActionWhere is null ? companyWhere : companyWhere.AndAlso(dynamicActionWhere);

        //return Ok(base.GetOdataListing(oDataQueryOptions, showDeletedRows, companyWhere));
        return Ok(base.GetOdataListing(oDataQueryOptions, showDeletedRows, finalWhere));
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

    private Expression<Func<Entity, bool>>? GetDynamicActionExpression(TypeAuthService typeAuthService, Access access)
    {
        //var dynamicAccess = true;

        //if (dynamicActionFilterOptions?.SimpleDynamicActionFilter is not null)
        //    dynamicAccess = typeAuthService.Can(dynamicActionFilterOptions.SimpleDynamicActionFilter, access, key);

        //if (dynamicActionFilterOptions?.SingleDynamicActionResolver is not null)
        //{
        //    if (dynamicActionFilterOptions?.SimpleDynamicActionFilter is not null)
        //        dynamicAccess = dynamicAccess || dynamicActionFilterOptions.SingleDynamicActionResolver(new DynamicActionResolver(this.HttpContext))(entity);
        //    else
        //        dynamicAccess = dynamicActionFilterOptions!.SingleDynamicActionResolver(new DynamicActionResolver(this.HttpContext))(entity);
        //}

        //return dynamicAccess;

        Expression<Func<Entity, bool>>? dynamicActionWhere = null;

        if (dynamicActionFilterOptions?.SimpleDynamicActionFilter is not null)
        {
            var accessibleIds = typeAuthService.GetAccessibleItems(dynamicActionFilterOptions.SimpleDynamicActionFilter, x => x == access);

            if (accessibleIds.WildCard)
            {
                dynamicActionWhere = x => true;
            }
            else
            {
                var ids = accessibleIds.AccessibleIds.Select(ShiftEntityHashIds.Decode<ListDTO>).ToList();

                dynamicActionWhere = x => ids.Contains(x.ID);
            }
        }

        if (dynamicActionFilterOptions?.ListingDynamicActionResolver is not null)
        {
            var listingWhere = dynamicActionFilterOptions.ListingDynamicActionResolver.Invoke(new DynamicActionResolver(this.HttpContext));

            dynamicActionWhere = dynamicActionWhere is null ? listingWhere : dynamicActionWhere.Or(listingWhere);
        }

        return dynamicActionWhere;
    }

    [Authorize]
    [HttpGet("{key}")]
    public virtual async Task<ActionResult<ShiftEntityResponse<SelectDTO>>> GetSingle(string key, [FromQuery] DateTime? asOf)
    {
        var typeAuthService = this.HttpContext.RequestServices.GetRequiredService<TypeAuthService>();

        if (!typeAuthService.CanRead(action))
            return Forbid();

        var result = (await base.GetSingleItem(key, asOf, GetDynamicActionExpression(typeAuthService, Access.Read)));

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
    public ShiftEntitySecureControllerAsync(ReadWriteDeleteAction action, Action<DynamicActionFilterOptions<Entity>>? dynamicActionFilterOptions = null) : base(action, dynamicActionFilterOptions)
    {

    }
}

public class DynamicActionResolver
{
    private readonly HttpContext httpContext;
    private readonly IServiceProvider serviceProvider;
    public DynamicActionResolver(HttpContext httpContext)
    {
        this.httpContext = httpContext;
        this.serviceProvider = httpContext.RequestServices;
    }
    public (bool WildCard, List<long> AccessibleIds) GetAccessibleIds<T>(DynamicAction action, string? selfId = null)
    {
        var accessibleItems = this.serviceProvider.GetRequiredService<TypeAuthService>().GetAccessibleItems(action, x => x == TypeAuth.Core.Access.Read, selfId);

        var decodedIds = new List<long>();

        if (!accessibleItems.WildCard)
        {
            decodedIds = accessibleItems.AccessibleIds.Select(x => ShiftEntityHashIds.Decode<T>(x)).ToList();
        }

        return (accessibleItems.WildCard, decodedIds);
    }

    public T? GetRequiredService<T>()
    {
        return this.serviceProvider.GetRequiredService<T>();
    }

    public long? GetUserId()
    {
        return httpContext.GetUserID();
    }
}

public class DynamicActionFilterOptions<Entity>
{
    /// <summary>
    /// Pass a Dynamic Action and the controller will use it on the ID field of the entity. If you need advanced filtering, use <typeparamref name="ListingDynamicActionResolver"/> instead
    /// </summary>
    public DynamicAction? SimpleDynamicActionFilter { get; set; }

    /// <summary>
    /// Used for more advanced filtering where filtering the ID by <typeparamref name="SimpleDynamicActionFilter"/> is not sufficient.
    /// </summary>
    public Func<DynamicActionResolver, Expression<Func<Entity, bool>>>? ListingDynamicActionResolver { get; set; }
}