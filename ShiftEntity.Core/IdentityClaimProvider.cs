using System.Collections.Generic;
using System.Security.Claims;

namespace ShiftSoftware.ShiftEntity.Core;

public class IdentityClaimProvider
{
    private readonly ClaimsPrincipal? user;
    private readonly IHashIdService hashIdService;

    public IdentityClaimProvider(ICurrentUserProvider currentUserProvider, IHashIdService hashIdService)
    {
        this.user = currentUserProvider.GetUser();
        this.hashIdService = hashIdService;
    }

    public long? GetCityID()
    {
        return this.user.GetCityID(this.hashIdService);
    }

    public long? GetCompanyBranchID()
    {
        return this.user.GetCompanyBranchID(this.hashIdService);
    }

    public long? GetCompanyID()
    {
        return this.user.GetCompanyID(this.hashIdService);
    }

    public long? GetCountryID()
    {
        return this.user.GetCountryID(this.hashIdService);
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
        return this.user.GetRegionID(this.hashIdService);
    }

    public List<long>? GetTeamIDs()
    {
        return this.user.GetTeamIDs(this.hashIdService);
    }

    public long? GetUserID()
    {
        return this.user.GetUserID(this.hashIdService);
    }

    public string? GetHashedUserID()
    {
        return this.user.GetHashedUserID();
    }
}
