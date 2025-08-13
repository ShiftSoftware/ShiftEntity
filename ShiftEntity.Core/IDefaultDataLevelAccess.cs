using System.Collections.Generic;

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
}