using Microsoft.AspNetCore.Http;
using System.Security.Claims;

namespace ShiftSoftware.ShiftEntity.Web.Extensions;

public static class HttpContextExtensions
{
    private static string? GetClaimValue(HttpContext httpContext, string claimId)
    {
        if (httpContext is null || httpContext.User.Identity is null || !httpContext.User.Identity.IsAuthenticated)
            return null;

        var value = httpContext.User.FindFirstValue(claimId);

        if (value is null)
            return null;

        return value;
    }
    private static long? GetDecodedClaimValue<T>(HttpContext httpContext, string claimId)
    {
        var value = GetClaimValue(httpContext, claimId);
        
        if (value is null)
            return null;

        return Services.ShiftEntityHashIds.Decode<T>(value);
    }

    internal static string? GetHashedRegionID(this HttpContext httpContext)
    {
        return GetClaimValue(httpContext, Core.Constants.RegionIdClaim);
    }
    internal static long? GetRegionID(this HttpContext httpContext)
    {
        return GetDecodedClaimValue<ShiftIdentity.Core.DTOs.Region.RegionDTO>(httpContext, Core.Constants.RegionIdClaim);
    }
    internal static string? GetHashedCompanyID(this HttpContext httpContext)
    {
        return GetClaimValue(httpContext, Core.Constants.CompanyIdClaim);
    }
    internal static long? GetCompanyID(this HttpContext httpContext)
    {
        return GetDecodedClaimValue<ShiftIdentity.Core.DTOs.Company.CompanyDTO>(httpContext, Core.Constants.CompanyIdClaim);
    }
    internal static string? GetHashedCompanyBranchID(this HttpContext httpContext)
    {
        return GetClaimValue(httpContext, Core.Constants.CompanyBranchIdClaim);
    }
    internal static long? GetCompanyBranchID(this HttpContext httpContext)
    {
        return GetDecodedClaimValue<ShiftIdentity.Core.DTOs.CompanyBranch.CompanyBranchDTO>(httpContext, Core.Constants.CompanyBranchIdClaim);
    }

    internal static long? GetUserID(this HttpContext httpContext)
    {
        return GetDecodedClaimValue<ShiftIdentity.Core.DTOs.User.UserDTO>(httpContext, ClaimTypes.NameIdentifier);
    }
}
