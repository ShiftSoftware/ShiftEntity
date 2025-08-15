using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Flags;
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

    public bool HasDefaultDataLevelAccess<EntityType>(
        DefaultDataLevelAccessOptions defaultDataLevelAccessOptions,
        EntityType? entity, Access access
    ) where EntityType : ShiftEntity<EntityType>, new()
    {
        var disableDefaultCountryFilter = defaultDataLevelAccessOptions.DisableDefaultCountryFilter;
        var disableDefaultRegionFilter = defaultDataLevelAccessOptions.DisableDefaultRegionFilter;
        var disableDefaultCompanyFilter = defaultDataLevelAccessOptions.DisableDefaultCompanyFilter;
        var disableDefaultCompanyBranchFilter = defaultDataLevelAccessOptions.DisableDefaultCompanyBranchFilter;
        var disableDefaultBrandFilter = defaultDataLevelAccessOptions.DisableDefaultBrandFilter;
        var disableDefaultCityFilter = defaultDataLevelAccessOptions.DisableDefaultCityFilter;
        var disableDefaultTeamFilter = defaultDataLevelAccessOptions.DisableDefaultTeamFilter;

        if (!disableDefaultCountryFilter)
        {
            if (entity is IEntityHasCountry<EntityType> entityWithCountry)
            {
                if (!typeAuthService.Can(
                    ShiftIdentityActions.DataLevelAccess.Countries,
                    access,
                    entityWithCountry?.CountryID is null ? TypeAuthContext.EmptyOrNullKey : ShiftEntityHashIdService.Encode<CountryDTO>(entityWithCountry.CountryID.Value),
                    this.httpContext.GetHashedCountryID()!
                ))
                {
                    return false;
                }
            }
        }

        if (!disableDefaultRegionFilter)
        {
            if (entity is IEntityHasRegion<EntityType> entityWithRegion)
            {
                if (!typeAuthService.Can(
                    ShiftIdentityActions.DataLevelAccess.Regions,
                    access,
                    entityWithRegion?.RegionID is null ? TypeAuthContext.EmptyOrNullKey : ShiftEntityHashIdService.Encode<RegionDTO>(entityWithRegion.RegionID.Value),
                    this.httpContext.GetHashedRegionID()!
                ))
                {
                    return false;
                }
            }
        }

        if (!disableDefaultCompanyFilter)
        {
            if (entity is IEntityHasCompany<EntityType> entityWithCompany)
            {
                if (!typeAuthService.Can(
                    ShiftIdentityActions.DataLevelAccess.Companies,
                    access,
                    entityWithCompany?.CompanyID is null ? TypeAuthContext.EmptyOrNullKey : ShiftEntityHashIdService.Encode<CompanyDTO>(entityWithCompany.CompanyID.Value),
                    this.httpContext.GetHashedCompanyID()!
                ))
                {
                    return false;
                }
            }
        }

        if (!disableDefaultCompanyBranchFilter)
        {
            if (entity is IEntityHasCompanyBranch<EntityType> entityWithCompanyBranch)
            {
                if (!typeAuthService.Can(
                    ShiftIdentityActions.DataLevelAccess.Branches,
                    access,
                    entityWithCompanyBranch?.CompanyBranchID is null ? TypeAuthContext.EmptyOrNullKey : ShiftEntityHashIdService.Encode<CompanyBranchDTO>(entityWithCompanyBranch.CompanyBranchID.Value),
                    this.httpContext.GetHashedCompanyBranchID()!
                ))
                {
                    return false;
                }
            }
        }

        if (!disableDefaultBrandFilter)
        {
            if (entity is IEntityHasBrand<EntityType> entityWithBrand)
            {
                if (!typeAuthService.Can(
                    ShiftIdentityActions.DataLevelAccess.Brands,
                    access,
                    entityWithBrand?.BrandID is null ? TypeAuthContext.EmptyOrNullKey : ShiftEntityHashIdService.Encode<BrandDTO>(entityWithBrand.BrandID.Value)
                ))
                {
                    return false;
                }
            }
        }

        if (!disableDefaultCityFilter)
        {
            if (entity is IEntityHasCity<EntityType> entityWithCity)
            {
                if (!typeAuthService.Can(
                    ShiftIdentityActions.DataLevelAccess.Cities,
                    access,
                    entityWithCity?.CityID is null ? TypeAuthContext.EmptyOrNullKey : ShiftEntityHashIdService.Encode<CityDTO>(entityWithCity.CityID.Value),
                    this.httpContext.GetHashedCityID()!
                ))
                {
                    return false;
                }
            }
        }

        if (!disableDefaultTeamFilter)
        {
            if (entity is IEntityHasTeam<EntityType> entityWithTeam)
            {
                if (!typeAuthService.Can(
                    ShiftIdentityActions.DataLevelAccess.Teams,
                    access,
                    entityWithTeam?.TeamID is null ? TypeAuthContext.EmptyOrNullKey : ShiftEntityHashIdService.Encode<TeamDTO>(entityWithTeam.TeamID.Value),
                    this.httpContext.GetHashedTeamIDs()?.ToArray()
                ))
                {
                    return false;
                }
            }
        }

        return true;
    }

    public IQueryable<EntityType> ApplyDefaultDataLevelFilters<EntityType>(DefaultDataLevelAccessOptions DefaultDataLevelAccessOptions, IQueryable<EntityType> query) where EntityType : ShiftEntity<EntityType>, new()
    {
        var disableDefaultCountryFilter = DefaultDataLevelAccessOptions.DisableDefaultCountryFilter;
        var disableDefaultRegionFilter = DefaultDataLevelAccessOptions.DisableDefaultRegionFilter;
        var disableDefaultCompanyFilter = DefaultDataLevelAccessOptions.DisableDefaultCompanyFilter;
        var disableDefaultCompanyBranchFilter = DefaultDataLevelAccessOptions.DisableDefaultCompanyBranchFilter;
        var disableDefaultBrandFilter = DefaultDataLevelAccessOptions.DisableDefaultBrandFilter;
        var disableDefaultCityFilter = DefaultDataLevelAccessOptions.DisableDefaultCityFilter;
        var disableDefaultTeamFilter = DefaultDataLevelAccessOptions.DisableDefaultTeamFilter;

        var entityHasCountry = typeof(EntityType).GetInterfaces().Any(x => x.IsAssignableFrom(typeof(IEntityHasCountry<EntityType>)));
        var entityHasRegion = typeof(EntityType).GetInterfaces().Any(x => x.IsAssignableFrom(typeof(IEntityHasRegion<EntityType>)));
        var entityHasCompany = typeof(EntityType).GetInterfaces().Any(x => x.IsAssignableFrom(typeof(IEntityHasCompany<EntityType>)));
        var entityHasCompanyBranch = typeof(EntityType).GetInterfaces().Any(x => x.IsAssignableFrom(typeof(IEntityHasCompanyBranch<EntityType>)));
        var entityHasBrand = typeof(EntityType).GetInterfaces().Any(x => x.IsAssignableFrom(typeof(IEntityHasBrand<EntityType>)));
        var entityHasCity = typeof(EntityType).GetInterfaces().Any(x => x.IsAssignableFrom(typeof(IEntityHasCity<EntityType>)));
        var entityHasTeam = typeof(EntityType).GetInterfaces().Any(x => x.IsAssignableFrom(typeof(IEntityHasTeam<EntityType>)));

        if (entityHasCountry && !disableDefaultCountryFilter)
        {
            List<long?>? accessibleCountries = this.GetAccessibleCountries();

            if (accessibleCountries is not null)
                query = query.Where(x => accessibleCountries.Contains((x as IEntityHasCountry<EntityType>)!.CountryID));
        }

        if (entityHasRegion && !disableDefaultRegionFilter)
        {
            List<long?>? accessibleRegions = this.GetAccessibleRegions();

            if (accessibleRegions is not null)
                query = query.Where(x => accessibleRegions.Contains((x as IEntityHasRegion<EntityType>)!.RegionID));
        }

        if (entityHasCompany && !disableDefaultCompanyFilter)
        {
            List<long?>? accessibleCompanies = this.GetAccessibleCompanies();

            if (accessibleCompanies is not null)
                query = query.Where(x => accessibleCompanies.Contains((x as IEntityHasCompany<EntityType>)!.CompanyID));
        }

        if (entityHasCompanyBranch && !disableDefaultCompanyBranchFilter)
        {
            List<long?>? accessibleBranches = this.GetAccessibleBranches();

            if (accessibleBranches is not null)
                query = query.Where(x => accessibleBranches.Contains((x as IEntityHasCompanyBranch<EntityType>)!.CompanyBranchID));
        }

        if (entityHasBrand && !disableDefaultBrandFilter)
        {
            List<long?>? accessibleBrands = this.GetAccessibleBrands();

            if (accessibleBrands is not null)
                query = query.Where(x => accessibleBrands.Contains((x as IEntityHasBrand<EntityType>)!.BrandID));
        }

        if (entityHasCity && !disableDefaultCityFilter)
        {
            List<long?>? accessibleCities = this.GetAccessibleCities();

            if (accessibleCities is not null)
                query = query.Where(x => accessibleCities.Contains((x as IEntityHasCity<EntityType>)!.CityID));
        }

        if (entityHasTeam && !disableDefaultTeamFilter)
        {
            List<long?>? accessibleTeams = this.GetAccessibleTeams();

            if (accessibleTeams is not null)
                query = query.Where(x => accessibleTeams.Contains((x as IEntityHasTeam<EntityType>)!.TeamID));
        }

        return query;
    }
}