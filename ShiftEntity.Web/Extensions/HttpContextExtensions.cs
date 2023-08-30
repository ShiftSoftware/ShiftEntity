using Microsoft.AspNetCore.Http;
using System.Security.Claims;

namespace ShiftSoftware.ShiftEntity.Web.Extensions;

public static class HttpContextExtensions
{
    private static long? GetDecodedClaimValue<T>(HttpContext httpContext, string claimId)
    {
        if (httpContext is null || httpContext.User.Identity is null || !httpContext.User.Identity.IsAuthenticated)
            return null;

        var value = httpContext.User.FindFirstValue(claimId);
        
        if (value is null)
            return null;

        return Services.ShiftEntityHashIds.Decode<T>(value);
    }

    internal static long? GetRegionID(this HttpContext httpContext)
    {
        return GetDecodedClaimValue<ShiftIdentity.Core.DTOs.Region.RegionDTO>(httpContext, Core.Constants.RegionIdClaim);
    }

    internal static long? GetCompanyID(this HttpContext httpContext)
    {
        return GetDecodedClaimValue<ShiftIdentity.Core.DTOs.Company.CompanyDTO>(httpContext, Core.Constants.CompanyIdClaim);
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
