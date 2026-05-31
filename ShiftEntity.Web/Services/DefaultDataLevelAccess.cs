using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Model.Flags;
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
using ShiftSoftware.TypeAuth.Core.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ShiftSoftware.ShiftEntity.Web.Services;

public class DefaultDataLevelAccess : IDefaultDataLevelAccess
{
    private readonly ITypeAuthService typeAuthService;
    private readonly IdentityClaimProvider identityClaimProvider;
    private readonly IHashIdService hashIdService;

    public DefaultDataLevelAccess(ITypeAuthService typeAuthService, IdentityClaimProvider identityClaimProvider, IHashIdService hashIdService)
    {
        this.typeAuthService = typeAuthService;
        this.identityClaimProvider = identityClaimProvider;
        this.hashIdService = hashIdService;
    }

    private List<long?>? GetAccessibleItems<TDto>(DynamicReadWriteDeleteAction claim, params string[]? selfId)
    {
        return typeAuthService
            .GetReadableItems(claim, selfId!)
            .ConvertIds<long>(x => hashIdService.Decode<TDto>(x));
    }

    public List<long?>? GetAccessibleCountries()
    {
        return GetAccessibleItems<CountryDTO>(
            ShiftIdentityActions.DataLevelAccess.Countries,
            identityClaimProvider?.GetHashedCountryID()!
        );
    }

    public List<long?>? GetAccessibleRegions()
    {
        return GetAccessibleItems<RegionDTO>(
            ShiftIdentityActions.DataLevelAccess.Regions,
            identityClaimProvider?.GetHashedRegionID()!
        );
    }

    public List<long?>? GetAccessibleCities()
    {
        return GetAccessibleItems<CityDTO>(
            ShiftIdentityActions.DataLevelAccess.Cities,
            identityClaimProvider?.GetHashedCityID()!
        );
    }

    public List<long?>? GetAccessibleCompanies()
    {
        return GetAccessibleItems<CompanyDTO>(
            ShiftIdentityActions.DataLevelAccess.Companies,
            identityClaimProvider?.GetHashedCompanyID()!
        );
    }

    public List<long?>? GetAccessibleBranches()
    {
        return GetAccessibleItems<CompanyBranchDTO>(
            ShiftIdentityActions.DataLevelAccess.Branches,
            identityClaimProvider?.GetHashedCompanyBranchID()!
        );
    }

    public List<long?>? GetAccessibleTeams()
    {
        return GetAccessibleItems<TeamDTO>(
            ShiftIdentityActions.DataLevelAccess.Teams,
            identityClaimProvider?.GetHashedTeamIDs()?.ToArray()!
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
                    entityWithCountry?.CountryID is null ? null : hashIdService.Encode<CountryDTO>(entityWithCountry.CountryID.Value),
                    this.identityClaimProvider.GetHashedCountryID()!
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
                    entityWithRegion?.RegionID is null ? null : hashIdService.Encode<RegionDTO>(entityWithRegion.RegionID.Value),
                    this.identityClaimProvider.GetHashedRegionID()!
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
                    entityWithCompany?.CompanyID is null ? null : hashIdService.Encode<CompanyDTO>(entityWithCompany.CompanyID.Value),
                    this.identityClaimProvider.GetHashedCompanyID()!
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
                    entityWithCompanyBranch?.CompanyBranchID is null ? null : hashIdService.Encode<CompanyBranchDTO>(entityWithCompanyBranch.CompanyBranchID.Value),
                    this.identityClaimProvider.GetHashedCompanyBranchID()!
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
                    entityWithBrand?.BrandID is null ? null : hashIdService.Encode<BrandDTO>(entityWithBrand.BrandID.Value)
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
                    entityWithCity?.CityID is null ? null : hashIdService.Encode<CityDTO>(entityWithCity.CityID.Value),
                    this.identityClaimProvider.GetHashedCityID()!
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
                    entityWithTeam?.TeamID is null ? null : hashIdService.Encode<TeamDTO>(entityWithTeam.TeamID.Value),
                    this.identityClaimProvider.GetHashedTeamIDs()?.ToArray()
                ))
                {
                    return false;
                }
            }
        }

        return true;
    }

    public IQueryable<EntityType> ApplyDefaultDataLevelFilters<EntityType>(DefaultDataLevelAccessOptions DefaultDataLevelAccessOptions, IQueryable<EntityType> query) where EntityType : notnull
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
            query = query.WhereIn(this.GetAccessibleCountries(), x => ((IEntityHasCountry<EntityType>)x).CountryID);

        if (entityHasRegion && !disableDefaultRegionFilter)
            query = query.WhereIn(this.GetAccessibleRegions(), x => ((IEntityHasRegion<EntityType>)x).RegionID);

        if (entityHasCompany && !disableDefaultCompanyFilter)
            query = query.WhereIn(this.GetAccessibleCompanies(), x => ((IEntityHasCompany<EntityType>)x).CompanyID);

        if (entityHasCompanyBranch && !disableDefaultCompanyBranchFilter)
            query = query.WhereIn(this.GetAccessibleBranches(), x => ((IEntityHasCompanyBranch<EntityType>)x).CompanyBranchID);

        if (entityHasBrand && !disableDefaultBrandFilter)
            query = query.WhereIn(this.GetAccessibleBrands(), x => ((IEntityHasBrand<EntityType>)x).BrandID);

        if (entityHasCity && !disableDefaultCityFilter)
            query = query.WhereIn(this.GetAccessibleCities(), x => ((IEntityHasCity<EntityType>)x).CityID);

        if (entityHasTeam && !disableDefaultTeamFilter)
            query = query.WhereIn(this.GetAccessibleTeams(), x => ((IEntityHasTeam<EntityType>)x).TeamID);

        return query;
    }

    public IQueryable<EntityType> ApplyDefaultCountryFilter<EntityType>(IQueryable<EntityType> query) where EntityType : IEntityHasCountry<EntityType>
    {
        return query.WhereIn(this.GetAccessibleCountries(), x => x.CountryID);
    }

    public IQueryable<EntityType> ApplyDefaultRegionFilter<EntityType>(IQueryable<EntityType> query) where EntityType : IEntityHasRegion<EntityType>
    {
        return query.WhereIn(this.GetAccessibleRegions(), x => x.RegionID);
    }

    public IQueryable<EntityType> ApplyDefaultCompanyFilter<EntityType>(IQueryable<EntityType> query) where EntityType : IEntityHasCompany<EntityType>
    {
        return query.WhereIn(this.GetAccessibleCompanies(), x => x.CompanyID);
    }

    public IQueryable<EntityType> ApplyDefaultBranchFilter<EntityType>(IQueryable<EntityType> query) where EntityType : IEntityHasCompanyBranch<EntityType>
    {
        return query.WhereIn(this.GetAccessibleBranches(), x => x.CompanyBranchID);
    }

    public IQueryable<EntityType> ApplyDefaultBrandFilter<EntityType>(IQueryable<EntityType> query) where EntityType : IEntityHasBrand<EntityType>
    {
        return query.WhereIn(this.GetAccessibleBrands(), x => x.BrandID);
    }

    public IQueryable<EntityType> ApplyDefaultCityFilter<EntityType>(IQueryable<EntityType> query) where EntityType : IEntityHasCity<EntityType>
    {
        return query.WhereIn(this.GetAccessibleCities(), x => x.CityID);
    }

    public IQueryable<EntityType> ApplyDefaultTeamFilter<EntityType>(IQueryable<EntityType> query) where EntityType : IEntityHasTeam<EntityType>
    {
        return query.WhereIn(this.GetAccessibleTeams(), x => x.TeamID);
    }
}