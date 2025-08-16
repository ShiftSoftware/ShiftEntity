using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using ShiftSoftware.ShiftEntity.Core;
using System.Collections.Generic;

namespace ShiftSoftware.ShiftEntity.Web.Services;

public class IdentityClaimProvider : IIdentityClaimProvider
{
    private readonly HttpContext context;
    public IdentityClaimProvider(IHttpContextAccessor httpContextAccessor)
    {
        this.context = httpContextAccessor.HttpContext!;
    }

    public long? GetCityID()
    {
        return this.context.GetCityID();
    }

    public long? GetCompanyBranchID()
    {
        return this.context.GetCompanyBranchID();
    }

    public long? GetCompanyID()
    {
        return this.context.GetCompanyID();
    }

    public long? GetCountryID()
    {
        return this.context.GetCountryID();
    }

    public string? GetHashedCityID()
    {
        return this.context.GetHashedCityID();
    }

    public string? GetHashedCompanyBranchID()
    {
        return this.context.GetHashedCompanyBranchID();
    }

    public string? GetHashedCompanyID()
    {
        return this.context.GetHashedCompanyID();
    }

    public string? GetHashedCountryID()
    {
        return this.context.GetHashedCountryID();
    }

    public string? GetHashedRegionID()
    {
        return this.context.GetHashedRegionID();
    }

    public List<string>? GetHashedTeamIDs()
    {
        return this.context.GetHashedTeamIDs();
    }

    public long? GetRegionID()
    {
        return this.context.GetRegionID();
    }

    public List<long>? GetTeamIDs()
    {
        return this.context.GetTeamIDs();
    }

    public long? GetUserID()
    {
        return this.context.GetUserID();
    }

    public string? GetUserStringID()
    {
        return this.context.GetUserStringID();
    }
}