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
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

namespace ShiftSoftware.ShiftEntity.Web.Services;

public class DefaultDataLevelAccess : IDefaultDataLevelAccess
{
    private readonly ITypeAuthService typeAuthService;
    private readonly IIdentityClaimProvider identityClaimProvider;

    public DefaultDataLevelAccess(ITypeAuthService typeAuthService, IIdentityClaimProvider identityClaimProvider)
    {
        this.typeAuthService = typeAuthService;
        this.identityClaimProvider = identityClaimProvider;
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
                    entityWithCountry?.CountryID is null ? TypeAuthContext.EmptyOrNullKey : ShiftEntityHashIdService.Encode<CountryDTO>(entityWithCountry.CountryID.Value),
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
                    entityWithRegion?.RegionID is null ? TypeAuthContext.EmptyOrNullKey : ShiftEntityHashIdService.Encode<RegionDTO>(entityWithRegion.RegionID.Value),
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
                    entityWithCompany?.CompanyID is null ? TypeAuthContext.EmptyOrNullKey : ShiftEntityHashIdService.Encode<CompanyDTO>(entityWithCompany.CompanyID.Value),
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
                    entityWithCompanyBranch?.CompanyBranchID is null ? TypeAuthContext.EmptyOrNullKey : ShiftEntityHashIdService.Encode<CompanyBranchDTO>(entityWithCompanyBranch.CompanyBranchID.Value),
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
                    entityWithTeam?.TeamID is null ? TypeAuthContext.EmptyOrNullKey : ShiftEntityHashIdService.Encode<TeamDTO>(entityWithTeam.TeamID.Value),
                    this.identityClaimProvider.GetHashedTeamIDs()?.ToArray()
                ))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private IQueryable<T> ApplyFilter<T>(List<long?>? data, IQueryable<T> query, Expression<Func<T, long?>> valueSelector)
    {
        if (data is null)
            return query;

        return query.Where(Expression.Lambda<Func<T, bool>>(
            Expression.Call(
                Expression.Constant(data),
                typeof(List<long?>).GetMethod("Contains", new[] { typeof(long?) })!,
                valueSelector.Body
            ),
            valueSelector.Parameters
        ));
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
            query = ApplyFilter(this.GetAccessibleCountries(), query, x => ((IEntityHasCountry<EntityType>)x).CountryID);

        if (entityHasRegion && !disableDefaultRegionFilter)
            query = ApplyFilter(this.GetAccessibleRegions(), query, x => ((IEntityHasRegion<EntityType>)x).RegionID);

        if (entityHasCompany && !disableDefaultCompanyFilter)
            query = ApplyFilter(this.GetAccessibleCompanies(), query, x => ((IEntityHasCompany<EntityType>)x).CompanyID);

        if (entityHasCompanyBranch && !disableDefaultCompanyBranchFilter)
            query = ApplyFilter(this.GetAccessibleBranches(), query, x => ((IEntityHasCompanyBranch<EntityType>)x).CompanyBranchID);

        if (entityHasBrand && !disableDefaultBrandFilter)
            query = ApplyFilter(this.GetAccessibleBrands(), query, x => ((IEntityHasBrand<EntityType>)x).BrandID);

        if (entityHasCity && !disableDefaultCityFilter)
            query = ApplyFilter(this.GetAccessibleCities(), query, x => ((IEntityHasCity<EntityType>)x).CityID);

        if (entityHasTeam && !disableDefaultTeamFilter)
            query = ApplyFilter(this.GetAccessibleTeams(), query, x => ((IEntityHasTeam<EntityType>)x).TeamID);

        return query;
    }

    public IQueryable<EntityType> ApplyDefaultFilterOnCountries<EntityType>(IQueryable<EntityType> query) where EntityType : IEntityHasCountry<EntityType>
    {
        return ApplyFilter(this.GetAccessibleCountries(), query, x => x.CountryID);
    }

    public IQueryable<EntityType> ApplyDefaultFilterOnRegions<EntityType>(IQueryable<EntityType> query) where EntityType : IEntityHasRegion<EntityType>
    {
        return ApplyFilter(this.GetAccessibleRegions(), query, x => x.RegionID);
    }

    public IQueryable<EntityType> ApplyDefaultFilterOnCompanies<EntityType>(IQueryable<EntityType> query) where EntityType : IEntityHasCompany<EntityType>
    {
        return ApplyFilter(this.GetAccessibleCompanies(), query, x => x.CompanyID);
    }

    public IQueryable<EntityType> ApplyDefaultFilterOnBranches<EntityType>(IQueryable<EntityType> query) where EntityType : IEntityHasCompanyBranch<EntityType>
    {
        return ApplyFilter(this.GetAccessibleBranches(), query, x => x.CompanyBranchID);
    }

    public IQueryable<EntityType> ApplyDefaultFilterOnBrands<EntityType>(IQueryable<EntityType> query) where EntityType : IEntityHasBrand<EntityType>
    {
        return ApplyFilter(this.GetAccessibleBrands(), query, x => x.BrandID);
    }

    public IQueryable<EntityType> ApplyDefaultFilterOnCities<EntityType>(IQueryable<EntityType> query) where EntityType : IEntityHasCity<EntityType>
    {
        return ApplyFilter(this.GetAccessibleCities(), query, x => x.CityID);
    }

    public IQueryable<EntityType> ApplyDefaultFilterOnTeams<EntityType>(IQueryable<EntityType> query) where EntityType : IEntityHasTeam<EntityType>
    {
        return ApplyFilter(this.GetAccessibleTeams(), query, x => x.TeamID);
    }
}