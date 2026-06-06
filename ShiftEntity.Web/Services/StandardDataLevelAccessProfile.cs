using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.DataLevelAccess;
using ShiftSoftware.ShiftEntity.Model.Flags;
using ShiftSoftware.ShiftIdentity.Core;
using ShiftSoftware.ShiftIdentity.Core.DTOs.Company;
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
/// Built one dimension per slice — 4.1 Company; Country/Region/Branch/Brand/City/Team follow (4.2–4.7) — each
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
    /// enforce it. Currently covers: <b>Company</b> (<see cref="IEntityHasCompany{Entity}"/>, slice 4.1).
    /// </summary>
    public static DataLevelAccessBuilder<TEntity> AddStandardDimensions<TEntity>(
        this DataLevelAccessBuilder<TEntity> access, DefaultDataLevelAccessOptions options)
    {
        if (access is null)
            throw new ArgumentNullException(nameof(access));
        if (options is null)
            throw new ArgumentNullException(nameof(options));

        // Company (4.1) — the legacy single-column dimension verbatim: ids granted on the Companies action,
        // hashid-encoded as CompanyDTO, with the caller's own (hashed) company claim resolving a self-reference
        // grant. The column is the marker's CompanyID, cast exactly as the legacy query filter casts it.
        if (!options.DisableDefaultCompanyFilter && typeof(IEntityHasCompany<TEntity>).IsAssignableFrom(typeof(TEntity)))
            access.On(ShiftIdentityActions.DataLevelAccess.Companies)
                .Key(x => ((IEntityHasCompany<TEntity>)x!).CompanyID)
                .HashId<CompanyDTO>()
                .Self(Core.Constants.CompanyIdClaim); // the claim ClaimsPrincipalExtensions.GetHashedCompanyID reads

        return access;
    }
}
