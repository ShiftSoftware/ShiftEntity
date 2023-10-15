using Microsoft.AspNetCore.Http;
using System.Security.Claims;

namespace Microsoft.AspNetCore.Mvc;

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

        return ShiftSoftware.ShiftEntity.Web.Services.ShiftEntityHashIds.Decode<T>(value);
    }

    internal static string? GetHashedRegionID(this HttpContext httpContext)
    {
        return GetClaimValue(httpContext, ShiftSoftware.ShiftEntity.Core.Constants.RegionIdClaim);
    }
    internal static long? GetRegionID(this HttpContext httpContext)
    {
        return GetDecodedClaimValue<ShiftSoftware.ShiftIdentity.Core.DTOs.Region.RegionDTO>(httpContext, ShiftSoftware.ShiftEntity.Core.Constants.RegionIdClaim);
    }
    internal static string? GetHashedCompanyID(this HttpContext httpContext)
    {
        return GetClaimValue(httpContext, ShiftSoftware.ShiftEntity.Core.Constants.CompanyIdClaim);
    }
    internal static long? GetCompanyID(this HttpContext httpContext)
    {
        return GetDecodedClaimValue<ShiftSoftware.ShiftIdentity.Core.DTOs.Company.CompanyDTO>(httpContext, ShiftSoftware.ShiftEntity.Core.Constants.CompanyIdClaim);
    }
    internal static string? GetHashedCompanyBranchID(this HttpContext httpContext)
    {
        return GetClaimValue(httpContext, ShiftSoftware.ShiftEntity.Core.Constants.CompanyBranchIdClaim);
    }
    internal static long? GetCompanyBranchID(this HttpContext httpContext)
    {
        return GetDecodedClaimValue<ShiftSoftware.ShiftIdentity.Core.DTOs.CompanyBranch.CompanyBranchDTO>(httpContext, ShiftSoftware.ShiftEntity.Core.Constants.CompanyBranchIdClaim);
    }

    internal static long? GetUserID(this HttpContext httpContext)
    {
        return GetDecodedClaimValue<ShiftSoftware.ShiftIdentity.Core.DTOs.User.UserDTO>(httpContext, ClaimTypes.NameIdentifier);
    }
}
