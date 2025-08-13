using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Model.HashIds;
using ShiftSoftware.ShiftIdentity.Core.DTOs.Country;
using ShiftSoftware.TypeAuth.Core;
using System.Collections.Generic;
using System.Linq;

namespace ShiftSoftware.ShiftEntity.Web.Services;

public class DefaultDataLevelAccess : IDefaultDataLevelAccess
{
    private readonly ITypeAuthService typeAuthService;
    private IHttpContextAccessor httpContextAccessor;

    public DefaultDataLevelAccess(ITypeAuthService typeAuthService, IHttpContextAccessor httpContextAccessor)
    {
        this.typeAuthService = typeAuthService;
        this.httpContextAccessor = httpContextAccessor;
    }

    public List<long?>? GetAccessibleCountries()
    {
        var accessibleCountriesTypeAuth = typeAuthService
            .GetAccessibleItems(
                ShiftIdentity.Core.ShiftIdentityActions.DataLevelAccess.Countries,
                x => x == TypeAuth.Core.Access.Read,
                this.httpContextAccessor?.HttpContext?.GetHashedCountryID()!
            );

        List<long?>? accessibleCountries = accessibleCountriesTypeAuth.WildCard ? null :
            accessibleCountriesTypeAuth
            .AccessibleIds
            .Select(x =>
                    x == TypeAuthContext.EmptyOrNullKey ? null :
                    (long?)ShiftEntityHashIdService.Decode<CountryDTO>(x)
                    )
            .ToList();

        return accessibleCountries;
    }

    public List<long?>? GetAccessibleRegions()
    {
        return null;
    }

    public List<long?>? GetAccessibleCities()
    {
        return null;
    }

    public List<long?>? GetAccessibleCompanies()
    {
        return null;
    }

    public List<long?>? GetAccessibleBranches()
    {
        return null;
    }

    public List<long?>? GetAccessibleTeams()
    {
        return null;
    }

    public List<long?>? GetAccessibleBrands()
    {
        return null;
    }
}