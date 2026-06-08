using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.DataLevelAccess;
using ShiftSoftware.ShiftEntity.Model.Flags;
using ShiftSoftware.ShiftIdentity.Core;
using ShiftSoftware.ShiftIdentity.Core.DTOs.City;
using ShiftSoftware.ShiftIdentity.Core.DTOs.Company;
using ShiftSoftware.ShiftIdentity.Core.DTOs.CompanyBranch;
using ShiftSoftware.ShiftIdentity.Core.DTOs.Country;
using ShiftSoftware.ShiftIdentity.Core.DTOs.Region;
using System;

namespace ShiftSoftware.ShiftEntity.Web.Services;

/// <summary>
/// The standard data-level profile on the v2 engine: declares, per marker interface, the same dimension the legacy
/// <see cref="DefaultDataLevelAccess"/> enforces — same TypeAuth action, same single key column, same hashid DTO
/// type-key, same self claim, honoring the same <see cref="DefaultDataLevelAccessOptions"/> disable flags. Parity
/// with legacy is the charter (Phase 4): an entity moved onto the profile must see byte-for-byte the rows it sees
/// today (the cross-column OR and the other v2 capabilities remain explicit, per-entity declarations).
/// </summary>
/// <remarks>
/// Built one dimension per slice — 4.1 Company, 4.2 Country; Region/Branch/Brand/City/Team follow (4.3–4.7) — each
/// proven against the real legacy implementation by the parity tests. The profile only <em>declares</em> dimensions;
/// nothing routes entities onto it automatically yet (that flip is decided once all seven are at parity). Note an
/// entity whose markers are all flag-disabled (or that has no markers) gets <em>no</em> dimensions — compiling a
/// policy from such an empty declaration throws by design (fail closed), so the future auto-wiring must declare a
/// policy only when at least one dimension landed.
/// </remarks>
public static class StandardDataLevelAccessProfile
{
    /// <summary>
    /// Adds the standard dimension for each marker interface <typeparamref name="TEntity"/> implements (unless the
    /// corresponding <paramref name="options"/> flag disables it), exactly as the legacy default filters would
    /// enforce it — one block per dimension, in the legacy dimension order. Currently covers:
    /// <b>Country</b> (<see cref="IEntityHasCountry{Entity}"/>, slice 4.2),
    /// <b>Region</b> (<see cref="IEntityHasRegion{Entity}"/>, slice 4.3),
    /// <b>Company</b> (<see cref="IEntityHasCompany{Entity}"/>, slice 4.1),
    /// <b>Branch</b> (<see cref="IEntityHasCompanyBranch{Entity}"/>, slice 4.4) and
    /// <b>City</b> (<see cref="IEntityHasCity{Entity}"/>, slice 4.6).
    /// </summary>
    public static DataLevelAccessBuilder<TEntity> AddStandardDimensions<TEntity>(
        this DataLevelAccessBuilder<TEntity> access, DefaultDataLevelAccessOptions options)
    {
        if (access is null)
            throw new ArgumentNullException(nameof(access));
        if (options is null)
            throw new ArgumentNullException(nameof(options));

        // Each block is the legacy single-column dimension verbatim: ids granted on the dimension's TypeAuth
        // action, hashid-encoded as the dimension's DTO type-key, with the caller's own (hashed) id claim — the
        // exact claim ClaimsPrincipalExtensions reads — resolving a self-reference grant. The column is the
        // marker's, cast exactly as the legacy query filter casts it.

        // Country (4.2)
        if (!options.DisableDefaultCountryFilter && typeof(IEntityHasCountry<TEntity>).IsAssignableFrom(typeof(TEntity)))
            access.On(ShiftIdentityActions.DataLevelAccess.Countries)
                .Key(x => ((IEntityHasCountry<TEntity>)x!).CountryID)
                .HashId<CountryDTO>()
                .Self(Core.Constants.CountryIdClaim);

        // Region (4.3)
        if (!options.DisableDefaultRegionFilter && typeof(IEntityHasRegion<TEntity>).IsAssignableFrom(typeof(TEntity)))
            access.On(ShiftIdentityActions.DataLevelAccess.Regions)
                .Key(x => ((IEntityHasRegion<TEntity>)x!).RegionID)
                .HashId<RegionDTO>()
                .Self(Core.Constants.RegionIdClaim);

        // Company (4.1)
        if (!options.DisableDefaultCompanyFilter && typeof(IEntityHasCompany<TEntity>).IsAssignableFrom(typeof(TEntity)))
            access.On(ShiftIdentityActions.DataLevelAccess.Companies)
                .Key(x => ((IEntityHasCompany<TEntity>)x!).CompanyID)
                .HashId<CompanyDTO>()
                .Self(Core.Constants.CompanyIdClaim);

        // Branch (4.4) — the action is `Branches` while the marker/flag/claim say `CompanyBranch`, an asymmetry
        // inherited verbatim from legacy.
        if (!options.DisableDefaultCompanyBranchFilter && typeof(IEntityHasCompanyBranch<TEntity>).IsAssignableFrom(typeof(TEntity)))
            access.On(ShiftIdentityActions.DataLevelAccess.Branches)
                .Key(x => ((IEntityHasCompanyBranch<TEntity>)x!).CompanyBranchID)
                .HashId<CompanyBranchDTO>()
                .Self(Core.Constants.CompanyBranchIdClaim);

        // City (4.6) — Brand (4.5) slots between Branch and City when it lands, keeping legacy's order.
        if (!options.DisableDefaultCityFilter && typeof(IEntityHasCity<TEntity>).IsAssignableFrom(typeof(TEntity)))
            access.On(ShiftIdentityActions.DataLevelAccess.Cities)
                .Key(x => ((IEntityHasCity<TEntity>)x!).CityID)
                .HashId<CityDTO>()
                .Self(Core.Constants.CityIdClaim);

        return access;
    }
}
