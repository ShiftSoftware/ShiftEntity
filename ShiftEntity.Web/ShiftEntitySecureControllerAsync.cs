﻿using Microsoft.AspNetCore.Mvc;
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
using ShiftSoftware.ShiftIdentity.Core.DTOs.Region;
using ShiftSoftware.ShiftIdentity.Core.DTOs.Company;
using ShiftSoftware.ShiftIdentity.Core.DTOs.CompanyBranch;
using System.Linq.Expressions;
using ShiftSoftware.TypeAuth.Core;
using ShiftSoftware.ShiftEntity.Model.HashIds;
using ShiftSoftware.ShiftEntity.Core.Services;
using ShiftSoftware.ShiftEntity.Print;

namespace ShiftSoftware.ShiftEntity.Web;

public class ShiftEntitySecureControllerAsync<Repository, Entity, ListDTO, ViewAndUpsertDTO> :
    ShiftEntityControllerBase<Repository, Entity, ListDTO, ViewAndUpsertDTO>
    where Repository : IShiftRepositoryAsync<Entity, ListDTO, ViewAndUpsertDTO>
    where Entity : ShiftEntity<Entity>, new()
    where ViewAndUpsertDTO : ShiftEntityViewAndUpsertDTO
    where ListDTO : ShiftEntityDTOBase
{
    private readonly ReadWriteDeleteAction action;

    private readonly DynamicActionFilterBuilder<Entity>? dynamicActionFilterBuilder;

    public ShiftEntitySecureControllerAsync(ReadWriteDeleteAction action, Action<DynamicActionFilterBuilder<Entity>>? dynamicActionFilterBuilder = null)
    {
        this.action = action;

        if (dynamicActionFilterBuilder is not null)
        {
            this.dynamicActionFilterBuilder = new();

            dynamicActionFilterBuilder.Invoke(this.dynamicActionFilterBuilder);
        }
    }

    [HttpGet]
    [EnableQueryWithHashIdConverter]
    [Authorize]
    public virtual ActionResult<ODataDTO<IQueryable<ListDTO>>> Get(ODataQueryOptions<ListDTO> oDataQueryOptions, [FromQuery] bool showDeletedRows = false)
    {
        var typeAuthService = this.HttpContext.RequestServices.GetRequiredService<ITypeAuthService>();

        if (!typeAuthService.CanRead(action))
            return Forbid();

        var accessibleRegionsTypeAuth = typeAuthService.GetAccessibleItems(ShiftIdentity.Core.ShiftIdentityActions.DataLevelAccess.Regions, x => x == TypeAuth.Core.Access.Read, this.HttpContext.GetHashedRegionID());
        var accessibleCompaniesTypeAuth = typeAuthService.GetAccessibleItems(ShiftIdentity.Core.ShiftIdentityActions.DataLevelAccess.Companies, x => x == TypeAuth.Core.Access.Read, this.HttpContext.GetHashedCompanyID());
        var accessibleBranchesTypeAuth = typeAuthService.GetAccessibleItems(ShiftIdentity.Core.ShiftIdentityActions.DataLevelAccess.Branches, x => x == TypeAuth.Core.Access.Read, this.HttpContext.GetHashedCompanyBranchID());

        List<long?>? accessibleRegions = accessibleRegionsTypeAuth.WildCard ? null : accessibleRegionsTypeAuth.AccessibleIds.Select(x => (long?)ShiftEntityHashIdService.Decode<RegionDTO>(x)).ToList();
        List<long?>? accessibleCompanies = accessibleCompaniesTypeAuth.WildCard ? null : accessibleCompaniesTypeAuth.AccessibleIds.Select(x => (long?)ShiftEntityHashIdService.Decode<CompanyDTO>(x)).ToList();
        List<long?>? accessibleBranches = accessibleBranchesTypeAuth.WildCard ? null : accessibleBranchesTypeAuth.AccessibleIds.Select(x => (long?)ShiftEntityHashIdService.Decode<CompanyBranchDTO>(x)).ToList();

        Expression<Func<Entity, bool>> companyWhere = x =>
            ((accessibleRegions == null || x.RegionID == null) ? true : accessibleRegions.Contains(x.RegionID)) &&
            ((accessibleCompanies == null || x.CompanyID == null) ? true : accessibleCompanies.Contains(x.CompanyID)) &&
            ((accessibleBranches == null || x.CompanyBranchID == null) ? true : accessibleBranches.Contains(x.CompanyBranchID));

        var dynamicActionWhere = GetDynamicActionExpression(typeAuthService, Access.Read, this.HttpContext.GetUserID());

        var finalWhere = dynamicActionWhere is null ? companyWhere : companyWhere.AndAlso(dynamicActionWhere);

        //return Ok(base.GetOdataListing(oDataQueryOptions, showDeletedRows, companyWhere));
        return Ok(base.GetOdataListing(oDataQueryOptions, showDeletedRows, finalWhere));
    }

    private bool HasDefaultDataLevelAccess(ITypeAuthService typeAuthService, Entity? entity, TypeAuth.Core.Access access)
    {
        if (entity?.RegionID is not null)
        {
            if (!typeAuthService.Can(ShiftIdentity.Core.ShiftIdentityActions.DataLevelAccess.Regions, access, ShiftEntityHashIdService.Encode<RegionDTO>(entity.RegionID.Value), this.HttpContext.GetHashedRegionID()))
                return false;
        }

        if (entity?.CompanyID is not null)
        {
            if (!typeAuthService.Can(ShiftIdentity.Core.ShiftIdentityActions.DataLevelAccess.Companies, access, ShiftEntityHashIdService.Encode<CompanyDTO>(entity.CompanyID.Value), this.HttpContext.GetHashedCompanyID()))
                return false;
        }

        if (entity?.CompanyBranchID is not null)
        {
            if (!typeAuthService.Can(ShiftIdentity.Core.ShiftIdentityActions.DataLevelAccess.Branches, access, ShiftEntityHashIdService.Encode<CompanyBranchDTO>(entity.CompanyBranchID.Value), this.HttpContext.GetHashedCompanyBranchID()))
                return false;
        }

        return true;
    }

    private Expression<Func<Entity, bool>>? GetDynamicActionExpression(ITypeAuthService typeAuthService, Access access, long? loggedInUserId)
    {
        Expression<Func<Entity, bool>>? dynamicActionWhere = null;

        if (dynamicActionFilterBuilder?.DynamicActionFilters is not null)
        {
            foreach (var filter in dynamicActionFilterBuilder.DynamicActionFilters)
            {
                var accessibleIds = typeAuthService.GetAccessibleItems(filter.DynamicAction, x => x == access);

                Expression<Func<Entity, bool>>? filterWhereExpression;

                if (accessibleIds.WildCard)
                {
                    filterWhereExpression = x => true;
                }
                else
                {
                    Type genericListType = typeof(List<>).MakeGenericType(filter.TKey);

                    var ids = Activator.CreateInstance(genericListType)!;

                    if (filter.TKey == typeof(long))
                    {
                        ids = accessibleIds.AccessibleIds.Select(x => filter.DTOType is null ? ShiftEntityHashIdService.Decode<ListDTO>(x) : long.Parse(x)).ToList();
                    }
                    else if (filter.TKey == typeof(long?))
                    {
                        ids = accessibleIds.AccessibleIds.Select(x => (long?)(filter.DTOType is null ? ShiftEntityHashIdService.Decode<ListDTO>(x) : long.Parse(x))).ToList();
                    }
                    else if (filter.TKey == typeof(int))
                    {
                        ids = accessibleIds.AccessibleIds.Select(x => filter.DTOType is null ? (int)ShiftEntityHashIdService.Decode<ListDTO>(x) : int.Parse(x)).ToList();
                    }
                    else if (filter.TKey == typeof(int?))
                    {
                        ids = accessibleIds.AccessibleIds.Select(x => (int?)(filter.DTOType is null ? ShiftEntityHashIdService.Decode<ListDTO>(x) : int.Parse(x))).ToList();
                    }

                    var containsMethod = ids.GetType().GetMethod(nameof(List<object>.Contains))!;

                    var idsExpression = Expression.Constant(ids, ids.GetType());

                    // Build expression for ids.Contains(x.ID)
                    var containsCall = Expression.Call(idsExpression, containsMethod, filter.InvocationExpression);

                    Expression finalContains = !filter.ShowNulls ? containsCall : Expression.OrElse(containsCall, Expression.Equal(filter.InvocationExpression, Expression.Constant(null)));

                    if (filter.CreatedByUserIDKeySelector is not null)
                    {
                        // Build expression for x.CreatedByUserID == loggedInUserId
                        var createdByKeySelectorInvoke = Expression.Invoke(filter.CreatedByUserIDKeySelector, filter.ParameterExpression);
                        //var createdByUserIdProperty = Expression.Property(parameter, nameof(ShiftEntity<Entity>.CreatedByUserID));

                        var loggedInUserIdExpression = Expression.Constant(loggedInUserId, typeof(long?));

                        var equalityComparison = Expression.Equal(createdByKeySelectorInvoke, loggedInUserIdExpression);

                        // Combine the two expressions with an OR condition
                        var orElse = Expression.OrElse(finalContains, equalityComparison);

                        filterWhereExpression = Expression.Lambda<Func<Entity, bool>>(orElse, filter.ParameterExpression); // x => ids.Contains(x.ID) || x.CreatedByUserID == loggedInUserId;
                    }
                    else
                        filterWhereExpression = Expression.Lambda<Func<Entity, bool>>(finalContains, filter.ParameterExpression); // x => ids.Contains(x.ID);
                }

                dynamicActionWhere = dynamicActionWhere is null ? filterWhereExpression : dynamicActionWhere.AndAlso(filterWhereExpression);
            }
        }

        if (dynamicActionFilterBuilder?.DynamicActionExpressionBuilder is not null)
        {
            var expressionBuilder = new DynamicActionExpressionBuilder(this.HttpContext.RequestServices, x => x == access, this.GetUserID());

            var dynamicActionExpression = dynamicActionFilterBuilder.DynamicActionExpressionBuilder.Invoke(expressionBuilder);

            dynamicActionWhere = dynamicActionWhere is null ? dynamicActionExpression : (expressionBuilder.CombineWithExistingFiltersWith == Operator.Or ? dynamicActionWhere.Or(dynamicActionExpression) : dynamicActionWhere.AndAlso(dynamicActionExpression));
        }

        return dynamicActionWhere;
    }

    [Authorize]
    [HttpGet("{key}")]
    public virtual async Task<ActionResult<ShiftEntityResponse<ViewAndUpsertDTO>>> GetSingle(string key, [FromQuery] DateTimeOffset? asOf)
    {
        var typeAuthService = this.HttpContext.RequestServices.GetRequiredService<ITypeAuthService>();

        if (!typeAuthService.CanRead(action))
            return Forbid();

        var result = await base.GetSingleItem(key, asOf, entity =>
        {
            var expression = GetDynamicActionExpression(typeAuthService, Access.Read, this.HttpContext.GetUserID());

            if (expression is not null)
            {
                if (!expression.Compile()(entity))
                    throw new ShiftEntityException(new Message("Error", "Unauthorized"), (int)System.Net.HttpStatusCode.Forbidden);
            }

            if (!HasDefaultDataLevelAccess(typeAuthService, entity, TypeAuth.Core.Access.Read))
                throw new ShiftEntityException(new Message("Error", "Unauthorized"), (int)System.Net.HttpStatusCode.Forbidden);
        });

        return result.ActionResult;
    }


    [HttpGet("print-token/{key}")]
    public virtual ActionResult PrintToken(string key)
    {
        var typeAuthService = this.HttpContext.RequestServices.GetRequiredService<ITypeAuthService>();
        var options = this.HttpContext.RequestServices.GetRequiredService<ShiftEntityPrintOptions>();

        if (!typeAuthService.CanRead(action))
            return Forbid();

        var url = Url.Action(nameof(Print), new { key = key });

        var (token, expires) = TokenService.GenerateSASToken(url!, key,
            DateTime.UtcNow.AddSeconds(options.TokenExpirationInSeconds), options.SASTokenKey);

        return Ok($"expires={expires}&token={token}");
    }

    [HttpGet("print/{key}")]
    [AllowAnonymous]
    public virtual async Task<ActionResult> Print(string key, [FromQuery] string? expires = null, [FromQuery] string? token = null)
    {
        var options = this.HttpContext.RequestServices.GetRequiredService<ShiftEntityPrintOptions>();

        var url = Url.Action(nameof(Print), new { key = key });

        if (!TokenService.ValidateSASToken(url!, key, expires!, token!, options.SASTokenKey))
            return Forbid();

        return (await base.Print(key));
    }

    [Authorize]
    [HttpGet]
    [EnableQueryWithHashIdConverter]
    public virtual async Task<ActionResult<ODataDTO<List<RevisionDTO>>>> GetRevisions(string key)
    {
        var typeAuthService = this.HttpContext.RequestServices.GetRequiredService<ITypeAuthService>();

        if (!typeAuthService.CanRead(action))
            return Forbid();

        return Ok(await base.GetRevisionListing(key));
    }

    [Authorize]
    [HttpPost]
    public virtual async Task<ActionResult<ShiftEntityResponse<ViewAndUpsertDTO>>> Post([FromBody] ViewAndUpsertDTO dto)
    {
        var typeAuthService = this.HttpContext.RequestServices.GetRequiredService<ITypeAuthService>();

        if (!typeAuthService.CanWrite(action))
            return Forbid();

        var result = await base.PostItem(dto, entity =>
        {
            var expression = GetDynamicActionExpression(typeAuthService, Access.Write, this.HttpContext.GetUserID());

            if (expression is not null)
            {
                if (!expression.Compile()(entity))
                    throw new ShiftEntityException(new Message("Error", "Unauthorized"), (int)System.Net.HttpStatusCode.Forbidden);
            }

            if (!HasDefaultDataLevelAccess(typeAuthService, entity, TypeAuth.Core.Access.Write))
                throw new ShiftEntityException(new Message("Error", "Unauthorized"), (int)System.Net.HttpStatusCode.Forbidden);
        });

        return result.ActionResult;
    }

    [Authorize]
    [HttpPut("{key}")]
    public virtual async Task<ActionResult<ShiftEntityResponse<ViewAndUpsertDTO>>> Put(string key, [FromBody] ViewAndUpsertDTO dto)
    {
        var typeAuthService = this.HttpContext.RequestServices.GetRequiredService<ITypeAuthService>();

        if (!typeAuthService.CanWrite(action))
            return Forbid();

        var result = await base.PutItem(key, dto, entity =>
        {
            var expression = GetDynamicActionExpression(typeAuthService, Access.Write, this.HttpContext.GetUserID());

            if (expression is not null)
            {
                if (!expression.Compile()(entity))
                    throw new ShiftEntityException(new Message("Error", "Unauthorized"), (int)System.Net.HttpStatusCode.Forbidden);
            }

            if (!HasDefaultDataLevelAccess(typeAuthService, entity, TypeAuth.Core.Access.Write))
                throw new ShiftEntityException(new Message("Error", "Unauthorized"), (int)System.Net.HttpStatusCode.Forbidden);
        });

        return result.ActionResult;
    }

    [Authorize]
    [HttpDelete("{key}")]
    public virtual async Task<ActionResult<ShiftEntityResponse<ViewAndUpsertDTO>>> Delete(string key, [FromQuery] bool isHardDelete = false)
    {
        var typeAuthService = this.HttpContext.RequestServices.GetRequiredService<ITypeAuthService>();

        if (!typeAuthService.CanDelete(action))
            return Forbid();

        var result = await base.DeleteItem(key, isHardDelete, entity =>
        {
            var expression = GetDynamicActionExpression(typeAuthService, Access.Delete, this.HttpContext.GetUserID());

            if (expression is not null)
            {
                if (!expression.Compile()(entity))
                    throw new ShiftEntityException(new Message("Error", "Unauthorized"), (int)System.Net.HttpStatusCode.Forbidden);
            }

            if (!HasDefaultDataLevelAccess(typeAuthService, entity, TypeAuth.Core.Access.Delete))
                throw new ShiftEntityException(new Message("Error", "Unauthorized"), (int)System.Net.HttpStatusCode.Forbidden);
        });

        return result.ActionResult;
    }
}