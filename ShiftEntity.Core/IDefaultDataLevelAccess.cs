using ShiftSoftware.ShiftEntity.Model.Flags;
using ShiftSoftware.TypeAuth.Core;
using System.Collections.Generic;
using System.Linq;

namespace ShiftSoftware.ShiftEntity.Core;

public interface IDefaultDataLevelAccess
{
    public List<long?>? GetAccessibleCountries();
    public List<long?>? GetAccessibleRegions();
    public List<long?>? GetAccessibleCompanies();
    public List<long?>? GetAccessibleBranches();
    public List<long?>? GetAccessibleBrands();
    public List<long?>? GetAccessibleCities();
    public List<long?>? GetAccessibleTeams();

    public IQueryable<EntityType> ApplyDefaultCountryFilter<EntityType>(IQueryable<EntityType> query) where EntityType : IEntityHasCountry<EntityType>;
    public IQueryable<EntityType> ApplyDefaultRegionFilter<EntityType>(IQueryable<EntityType> query) where EntityType : IEntityHasRegion<EntityType>;
    public IQueryable<EntityType> ApplyDefaultCompanyFilter<EntityType>(IQueryable<EntityType> query) where EntityType : IEntityHasCompany<EntityType>;
    public IQueryable<EntityType> ApplyDefaultBranchFilter<EntityType>(IQueryable<EntityType> query) where EntityType : IEntityHasCompanyBranch<EntityType>;
    public IQueryable<EntityType> ApplyDefaultBrandFilter<EntityType>(IQueryable<EntityType> query) where EntityType : IEntityHasBrand<EntityType>;
    public IQueryable<EntityType> ApplyDefaultCityFilter<EntityType>(IQueryable<EntityType> query) where EntityType : IEntityHasCity<EntityType>;
    public IQueryable<EntityType> ApplyDefaultTeamFilter<EntityType>(IQueryable<EntityType> query) where EntityType : IEntityHasTeam<EntityType>;


    public bool HasDefaultDataLevelAccess<EntityType>(DefaultDataLevelAccessOptions defaultDataLevelAccessOptions, EntityType? entity, Access access) where EntityType : ShiftEntity<EntityType>, new();
    public IQueryable<EntityType> ApplyDefaultDataLevelFilters<EntityType>(DefaultDataLevelAccessOptions DefaultDataLevelAccessOptions, IQueryable<EntityType> query) where EntityType : notnull;
}