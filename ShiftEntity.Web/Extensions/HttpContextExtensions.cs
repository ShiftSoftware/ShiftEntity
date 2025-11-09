using Microsoft.AspNetCore.Http;
using ShiftSoftware.ShiftEntity.Model.HashIds;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;

namespace Microsoft.AspNetCore.Mvc;

public static class HttpContextExtensions
{
    public static List<string>? GetClaimValues(this HttpContext httpContext, string claimId)
    {
        if (httpContext is null || httpContext.User.Identity is null || !httpContext.User.Identity.IsAuthenticated)
            return null;

        var value = httpContext.User.FindAll(claimId).Select(x => x.Value).ToList();

        if (value is null)
            return null;

        return value;
    }

    public static List<long>? GetDecodedClaimValues<T>(this HttpContext httpContext, string claimId)
    {
        var values = GetClaimValues(httpContext, claimId);
        
        if (values is null)
            return null;

        return values.Select(x => ShiftEntityHashIdService.Decode<T>(x)).ToList();
    }

    public static string? GetHashedRegionID(this HttpContext httpContext)
    {
        return GetClaimValues(httpContext, ShiftSoftware.ShiftEntity.Core.Constants.RegionIdClaim)?.FirstOrDefault();
    }

    public static long? GetRegionID(this HttpContext httpContext)
    {
        return GetDecodedClaimValues<ShiftSoftware.ShiftIdentity.Core.DTOs.Region.RegionDTO>(httpContext, ShiftSoftware.ShiftEntity.Core.Constants.RegionIdClaim)?.FirstOrDefault();
    }

    public static string? GetHashedCountryID(this HttpContext httpContext)
    {
        return GetClaimValues(httpContext, ShiftSoftware.ShiftEntity.Core.Constants.CountryIdClaim)?.FirstOrDefault();
    }

    public static long? GetCountryID(this HttpContext httpContext)
    {
        return GetDecodedClaimValues<ShiftSoftware.ShiftIdentity.Core.DTOs.Country.CountryDTO>(httpContext, ShiftSoftware.ShiftEntity.Core.Constants.CountryIdClaim)?.FirstOrDefault();
    }

    public static string? GetHashedCompanyID(this HttpContext httpContext)
    {
        return GetClaimValues(httpContext, ShiftSoftware.ShiftEntity.Core.Constants.CompanyIdClaim)?.FirstOrDefault();
    }

    public static long? GetCompanyID(this HttpContext httpContext)
    {
        return GetDecodedClaimValues<ShiftSoftware.ShiftIdentity.Core.DTOs.Company.CompanyDTO>(httpContext, ShiftSoftware.ShiftEntity.Core.Constants.CompanyIdClaim)?.FirstOrDefault();
    }

    public static string? GetHashedCityID(this HttpContext httpContext)
    {
        return GetClaimValues(httpContext, ShiftSoftware.ShiftEntity.Core.Constants.CityIdClaim)?.FirstOrDefault();
    }

    public static long? GetCityID(this HttpContext httpContext)
    {
        return GetDecodedClaimValues<ShiftSoftware.ShiftIdentity.Core.DTOs.City.CityDTO>(httpContext, ShiftSoftware.ShiftEntity.Core.Constants.CityIdClaim)?.FirstOrDefault();
    }

    public static string? GetHashedCompanyBranchID(this HttpContext httpContext)
    {
        return GetClaimValues(httpContext, ShiftSoftware.ShiftEntity.Core.Constants.CompanyBranchIdClaim)?.FirstOrDefault();
    }

    public static long? GetCompanyBranchID(this HttpContext httpContext)
    {
        return GetDecodedClaimValues<ShiftSoftware.ShiftIdentity.Core.DTOs.CompanyBranch.CompanyBranchDTO>(httpContext, ShiftSoftware.ShiftEntity.Core.Constants.CompanyBranchIdClaim)?.FirstOrDefault();
    }

    public static long? GetUserID(this HttpContext httpContext)
    {
        return GetDecodedClaimValues<ShiftSoftware.ShiftIdentity.Core.DTOs.User.UserDTO>(httpContext, ClaimTypes.NameIdentifier)?.FirstOrDefault();
    }

    public static string? GetUserEmail(this HttpContext httpContext)
    {
        return GetClaimValues(httpContext, ClaimTypes.Email)?.FirstOrDefault();
    }

    public static string? GetHashedUserID(this HttpContext httpContext)
    {
        return GetClaimValues(httpContext, ClaimTypes.NameIdentifier)?.FirstOrDefault();
    }

    public static List<string>? GetHashedTeamIDs(this HttpContext httpContext)
    {
        return GetClaimValues(httpContext, ShiftSoftware.ShiftEntity.Core.Constants.TeamIdsClaim);
    }

    public static List<long>? GetTeamIDs(this HttpContext httpContext)
    {
        return GetDecodedClaimValues<ShiftSoftware.ShiftIdentity.Core.DTOs.Team.TeamDTO>(httpContext, ShiftSoftware.ShiftEntity.Core.Constants.TeamIdsClaim);
    }
}
