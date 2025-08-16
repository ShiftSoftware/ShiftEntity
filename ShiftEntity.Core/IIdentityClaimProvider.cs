using System.Collections.Generic;

namespace ShiftSoftware.ShiftEntity.Core;

public interface IIdentityClaimProvider
{
    public string? GetHashedRegionID();
    public long? GetRegionID();
    public string? GetHashedCountryID();
    public long? GetCountryID();
    public string? GetHashedCompanyID();
    public long? GetCompanyID();
    public string? GetHashedCityID();
    public long? GetCityID();
    public string? GetHashedCompanyBranchID();
    public long? GetCompanyBranchID();
    public long? GetUserID();
    public string? GetUserStringID();
    public List<string>? GetHashedTeamIDs();
    public List<long>? GetTeamIDs();
}