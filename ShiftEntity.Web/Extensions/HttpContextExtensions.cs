﻿using Microsoft.AspNetCore.Http;
using ShiftSoftware.ShiftEntity.Model.HashIds;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;

namespace Microsoft.AspNetCore.Mvc;

public static class HttpContextExtensions
{
    private static List<string>? GetClaimValues(HttpContext httpContext, string claimId)
    {
        if (httpContext is null || httpContext.User.Identity is null || !httpContext.User.Identity.IsAuthenticated)
            return null;

        var value = httpContext.User.FindAll(claimId).Select(x => x.Value).ToList();

        if (value is null)
            return null;

        return value;
    }
    private static List<long>? GetDecodedClaimValues<T>(HttpContext httpContext, string claimId)
    {
        var values = GetClaimValues(httpContext, claimId);
        
        if (values is null)
            return null;

        return values.Select(x => ShiftEntityHashIdService.Decode<T>(x)).ToList();
    }

    internal static string? GetHashedRegionID(this HttpContext httpContext)
    {
        return GetClaimValues(httpContext, ShiftSoftware.ShiftEntity.Core.Constants.RegionIdClaim)?.FirstOrDefault();
    }
    internal static long? GetRegionID(this HttpContext httpContext)
    {
        return GetDecodedClaimValues<ShiftSoftware.ShiftIdentity.Core.DTOs.Region.RegionDTO>(httpContext, ShiftSoftware.ShiftEntity.Core.Constants.RegionIdClaim)?.FirstOrDefault();
    }
    internal static string? GetHashedCompanyID(this HttpContext httpContext)
    {
        return GetClaimValues(httpContext, ShiftSoftware.ShiftEntity.Core.Constants.CompanyIdClaim)?.FirstOrDefault();
    }
    internal static long? GetCompanyID(this HttpContext httpContext)
    {
        return GetDecodedClaimValues<ShiftSoftware.ShiftIdentity.Core.DTOs.Company.CompanyDTO>(httpContext, ShiftSoftware.ShiftEntity.Core.Constants.CompanyIdClaim)?.FirstOrDefault();
    }
    internal static string? GetHashedCompanyBranchID(this HttpContext httpContext)
    {
        return GetClaimValues(httpContext, ShiftSoftware.ShiftEntity.Core.Constants.CompanyBranchIdClaim)?.FirstOrDefault();
    }
    internal static long? GetCompanyBranchID(this HttpContext httpContext)
    {
        return GetDecodedClaimValues<ShiftSoftware.ShiftIdentity.Core.DTOs.CompanyBranch.CompanyBranchDTO>(httpContext, ShiftSoftware.ShiftEntity.Core.Constants.CompanyBranchIdClaim)?.FirstOrDefault();
    }

    internal static long? GetUserID(this HttpContext httpContext)
    {
        return GetDecodedClaimValues<ShiftSoftware.ShiftIdentity.Core.DTOs.User.UserDTO>(httpContext, ClaimTypes.NameIdentifier)?.FirstOrDefault();
    }

    internal static List<string>? GetHashedUserGroupIDs(this HttpContext httpContext)
    {
        return GetClaimValues(httpContext, ShiftSoftware.ShiftEntity.Core.Constants.UserGroupIdsClaim);
    }
    internal static List<long>? GetUserGroupIDs(this HttpContext httpContext)
    {
        return GetDecodedClaimValues<ShiftSoftware.ShiftIdentity.Core.DTOs.UserGroup.UserGroupDTO>(httpContext, ShiftSoftware.ShiftEntity.Core.Constants.UserGroupIdsClaim);
    }
}
