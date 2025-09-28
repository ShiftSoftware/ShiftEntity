using System.Collections.Generic;
using System.Security.Claims;

namespace ShiftSoftware.ShiftEntity.Core;

public class IdentityClaimProvider
{
    private readonly ClaimsPrincipal? user;

    public IdentityClaimProvider(ICurrentUserProvider currentUserProvider)
    {
        this.user = currentUserProvider.GetUser();
    }

    public long? GetCityID()
    {
        return this.user.GetCityID();
    }

    public long? GetCompanyBranchID()
    {
        return this.user.GetCompanyBranchID();
    }

    public long? GetCompanyID()
    {
        return this.user.GetCompanyID();
    }

    public long? GetCountryID()
    {
        return this.user.GetCountryID();
    }

    public string? GetHashedCityID()
    {
        return this.user.GetHashedCityID();
    }

    public string? GetHashedCompanyBranchID()
    {
        return this.user.GetHashedCompanyBranchID();
    }

    public string? GetHashedCompanyID()
    {
        return this.user.GetHashedCompanyID();
    }

    public string? GetHashedCountryID()
    {
        return this.user.GetHashedCountryID();
    }

    public string? GetHashedRegionID()
    {
        return this.user.GetHashedRegionID();
    }

    public List<string>? GetHashedTeamIDs()
    {
        return this.user.GetHashedTeamIDs();
    }

    public long? GetRegionID()
    {
        return this.user.GetRegionID();
    }

    public List<long>? GetTeamIDs()
    {
        return this.user.GetTeamIDs();
    }

    public long? GetUserID()
    {
        return this.user.GetUserID();
    }

    public string? GetHashedUserID()
    {
        return this.user.GetHashedUserID();
    }
}