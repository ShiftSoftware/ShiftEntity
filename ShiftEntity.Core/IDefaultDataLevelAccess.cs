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
    public bool HasDefaultDataLevelAccess<EntityType>(DefaultDataLevelAccessOptions defaultDataLevelAccessOptions, EntityType? entity, Access access) where EntityType : ShiftEntity<EntityType>, new();
    public IQueryable<EntityType> ApplyDefaultDataLevelFilters<EntityType>(DefaultDataLevelAccessOptions DefaultDataLevelAccessOptions, IQueryable<EntityType> query) where EntityType : ShiftEntity<EntityType>, new();
}