using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Model.HashIds;
using System.Collections.Generic;
using System.Linq;

namespace System.Security.Claims;

public static class ClaimsPrincipalExtensions
{
    public static List<string>? GetClaimValues(this ClaimsPrincipal? claimsPrincipal, string claimId)
    {
        if (claimsPrincipal is null || claimsPrincipal.Identity is null || !claimsPrincipal.Identity.IsAuthenticated)
            return null;

        var value = claimsPrincipal.FindAll(claimId).Select(x => x.Value).ToList();

        if (value is null)
            return null;

        return value;
    }

    public static List<long>? GetDecodedClaimValues(this ClaimsPrincipal? claimsPrincipal, string claimId, JsonHashIdConverterAttribute jsonHashIdConverterAttribute)
    {
        var values = GetClaimValues(claimsPrincipal, claimId);

        if (values is null)
            return null;

        return values.Select(x => ShiftEntityHashIdService.Decode(x, jsonHashIdConverterAttribute)).ToList();
    }

    public static string? GetHashedRegionID(this ClaimsPrincipal? claimsPrincipal)
    {
        return GetClaimValues(claimsPrincipal, Constants.RegionIdClaim)?.FirstOrDefault();
    }

    public static long? GetRegionID(this ClaimsPrincipal? claimsPrincipal)
    {
        return GetDecodedClaimValues(claimsPrincipal, Constants.RegionIdClaim, (new RegionHashIdConverter()))?.FirstOrDefault();
    }

    public static string? GetHashedCountryID(this ClaimsPrincipal? claimsPrincipal)
    {
        return GetClaimValues(claimsPrincipal, Constants.CountryIdClaim)?.FirstOrDefault();
    }

    public static long? GetCountryID(this ClaimsPrincipal? claimsPrincipal)
    {
        return GetDecodedClaimValues(claimsPrincipal, Constants.CountryIdClaim, (new CountryHashIdConverter()))?.FirstOrDefault();
    }

    public static string? GetHashedCompanyID(this ClaimsPrincipal? claimsPrincipal)
    {
        return GetClaimValues(claimsPrincipal, Constants.CompanyIdClaim)?.FirstOrDefault();
    }

    public static long? GetCompanyID(this ClaimsPrincipal? claimsPrincipal)
    {
        return GetDecodedClaimValues(claimsPrincipal, Constants.CompanyIdClaim, (new CompanyHashIdConverter()))?.FirstOrDefault();
    }

    public static string? GetHashedCityID(this ClaimsPrincipal? claimsPrincipal)
    {
        return GetClaimValues(claimsPrincipal, Constants.CityIdClaim)?.FirstOrDefault();
    }

    public static long? GetCityID(this ClaimsPrincipal? claimsPrincipal)
    {
        return GetDecodedClaimValues(claimsPrincipal, Constants.CityIdClaim, (new CityHashIdConverter()))?.FirstOrDefault();
    }

    public static string? GetHashedCompanyBranchID(this ClaimsPrincipal? claimsPrincipal)
    {
        return GetClaimValues(claimsPrincipal, Constants.CompanyBranchIdClaim)?.FirstOrDefault();
    }

    public static long? GetCompanyBranchID(this ClaimsPrincipal? claimsPrincipal)
    {
        return GetDecodedClaimValues(claimsPrincipal, Constants.CompanyBranchIdClaim, (new CompanyBranchHashIdConverter()))?.FirstOrDefault();
    }

    public static long? GetUserID(this ClaimsPrincipal? claimsPrincipal)
    {
        return GetDecodedClaimValues(claimsPrincipal, ClaimTypes.NameIdentifier, (new UserHashIdConverter()))?.FirstOrDefault();
    }

    public static string? GetUserStringID(this ClaimsPrincipal? claimsPrincipal)
    {
        return GetClaimValues(claimsPrincipal, ClaimTypes.NameIdentifier)?.FirstOrDefault();
    }

    public static List<string>? GetHashedTeamIDs(this ClaimsPrincipal? claimsPrincipal)
    {
        return GetClaimValues(claimsPrincipal, Constants.TeamIdsClaim);
    }

    public static List<long>? GetTeamIDs(this ClaimsPrincipal? claimsPrincipal)
    {
        return GetDecodedClaimValues(claimsPrincipal, Constants.TeamIdsClaim, (new TeamHashIdConverter()));
    }
}