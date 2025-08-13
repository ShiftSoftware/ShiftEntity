using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Model.HashIds;
using ShiftSoftware.ShiftIdentity.Core;
using ShiftSoftware.ShiftIdentity.Core.DTOs.Brand;
using ShiftSoftware.ShiftIdentity.Core.DTOs.City;
using ShiftSoftware.ShiftIdentity.Core.DTOs.Company;
using ShiftSoftware.ShiftIdentity.Core.DTOs.CompanyBranch;
using ShiftSoftware.ShiftIdentity.Core.DTOs.Country;
using ShiftSoftware.ShiftIdentity.Core.DTOs.Region;
using ShiftSoftware.ShiftIdentity.Core.DTOs.Team;
using ShiftSoftware.TypeAuth.Core;
using ShiftSoftware.TypeAuth.Core.Actions;
using System.Collections.Generic;
using System.Linq;

namespace ShiftSoftware.ShiftEntity.Web.Services;

public class DefaultDataLevelAccess : IDefaultDataLevelAccess
{
    private readonly ITypeAuthService typeAuthService;
    private readonly HttpContext httpContext;
    
    public DefaultDataLevelAccess(ITypeAuthService typeAuthService, IHttpContextAccessor httpContextAccessor)
    {
        this.typeAuthService = typeAuthService;
        this.httpContext = httpContextAccessor.HttpContext!;
    }

    private List<long?>? GetAccessibleItems<TDto>(DynamicReadWriteDeleteAction claim, params string[]? selfId)
    {
        var accessibleItemsTypeAuth = typeAuthService.GetAccessibleItems(claim, x => x == TypeAuth.Core.Access.Read, selfId!);

        List<long?>? accessibleItems = accessibleItemsTypeAuth.WildCard ? null :
            accessibleItemsTypeAuth
            .AccessibleIds
            .Select(x => x == TypeAuthContext.EmptyOrNullKey ? null : (long?)ShiftEntityHashIdService.Decode<TDto>(x))
            .ToList();

        return accessibleItems;
    }

    public List<long?>? GetAccessibleCountries()
    {
        return GetAccessibleItems<CountryDTO>(
            ShiftIdentityActions.DataLevelAccess.Countries,
            httpContext?.GetHashedCountryID()!
        );
    }

    public List<long?>? GetAccessibleRegions()
    {
        return GetAccessibleItems<RegionDTO>(
            ShiftIdentityActions.DataLevelAccess.Regions,
            httpContext?.GetHashedRegionID()!
        );
    }

    public List<long?>? GetAccessibleCities()
    {
        return GetAccessibleItems<CityDTO>(
            ShiftIdentityActions.DataLevelAccess.Cities,
            httpContext?.GetHashedCityID()!
        );
    }

    public List<long?>? GetAccessibleCompanies()
    {
        return GetAccessibleItems<CompanyDTO>(
            ShiftIdentityActions.DataLevelAccess.Companies,
            httpContext?.GetHashedCompanyID()!
        );
    }

    public List<long?>? GetAccessibleBranches()
    {
        return GetAccessibleItems<CompanyBranchDTO>(
            ShiftIdentityActions.DataLevelAccess.Branches,
            httpContext?.GetHashedCompanyBranchID()!
        );
    }

    public List<long?>? GetAccessibleTeams()
    {
        return GetAccessibleItems<TeamDTO>(
            ShiftIdentityActions.DataLevelAccess.Teams,
            httpContext?.GetHashedTeamIDs()?.ToArray()!
        );
    }

    public List<long?>? GetAccessibleBrands()
    {
        return GetAccessibleItems<BrandDTO>(ShiftIdentityActions.DataLevelAccess.Brands);
    }
}