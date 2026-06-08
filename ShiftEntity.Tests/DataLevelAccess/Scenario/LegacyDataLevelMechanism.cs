using ShiftSoftware.TypeAuth.Core;
using ShiftSoftware.TypeAuth.Core.Linq;

namespace ShiftSoftware.ShiftEntity.Tests.DataLevelAccess.Scenario;

/// <summary>
/// A faithful, ShiftIdentity-free reproduction of <b>today's</b> data-level mechanism for the Company
/// dimension, mirroring <c>ShiftEntity.Web/Services/DefaultDataLevelAccess.cs</c> so slice 0.3 can lock
/// current behavior as a golden master <i>without</i> wiring the ShiftIdentity-coupled class (per the
/// slice brief). Raw <see cref="long"/> ids stand in for hashids; Company is the scenario's only dimension.
/// <para>
/// Legacy source mapping (read the real thing for fidelity — but this does not depend on it):
/// <list type="bullet">
/// <item><b>Query path</b> ≈ <c>ApplyDefaultDataLevelFilters</c> → <c>ApplyDefaultCompanyFilter</c>:
/// <c>query.WhereIn(GetAccessibleCompanies(), x =&gt; x.CompanyID)</c>, where
/// <c>GetAccessibleCompanies()</c> = <c>GetReadableItems(Companies, self).ConvertIds&lt;long&gt;(decode)</c>
/// — <b>always at Read level</b>, and <b>single-column</b> (no <c>IntermediaryCompanyID</c> — the gap).</item>
/// <item><b>Row path</b> ≈ the Company block of <c>HasDefaultDataLevelAccess</c>:
/// <c>if (!disableCompany &amp;&amp; entity is IEntityHasCompany) if (!Can(Companies, access, encode(CompanyID), self)) return false;</c>
/// at the operation's level (Read=View, Write=Insert/Edit, Delete=Delete), honoring the disable flag.</item>
/// </list>
/// Phase 4 reproduces these against the v2 engine and proves parity / the fix.
/// </para>
/// </summary>
public static class LegacyDataLevelMechanism
{
    /// <summary>
    /// <c>GetAccessibleCompanies()</c>: the Read-level accessible company ids (<see langword="null"/> ⇒ wildcard,
    /// <c>EmptyOrNullKey</c> ⇒ <see langword="null"/> entry). The query path is always derived at Read level.
    /// </summary>
    public static List<long?>? AccessibleCompanies(TypeAuthContext ctx, string? selfCompanyId = null)
        => ctx.GetReadableItems(VehicleDataLevel.Companies, Self(selfCompanyId))
              .ConvertIds<long>(long.Parse);

    /// <summary>
    /// The legacy query path: a <b>single-column</b> <c>WhereIn</c> on <see cref="Vehicle.CompanyID"/> only.
    /// It cannot reach <see cref="Vehicle.IntermediaryCompanyID"/>, which is the cross-column OR gap.
    /// </summary>
    public static IQueryable<Vehicle> ApplyCompanyQueryFilter(IQueryable<Vehicle> query, TypeAuthContext ctx, string? selfCompanyId = null)
        => query.WhereIn(AccessibleCompanies(ctx, selfCompanyId), x => x.CompanyID);

    /// <summary>
    /// The custom OR query a consumer must hand-write today (≈ a <c>FilterByTypeAuthValues</c> escape hatch) to
    /// express "<c>CompanyID == mine OR IntermediaryCompanyID == mine</c>". Correct on the <b>read</b> path, but
    /// only usable with <c>DisableDefaultCompanyFilter = true</c> — otherwise it AND-collapses behind the default
    /// single-column filter (see the AND-trap characterization). <see langword="null"/> accessible set ⇒ wildcard.
    /// </summary>
    public static IQueryable<Vehicle> ApplyCompanyOrQueryFilter(IQueryable<Vehicle> query, TypeAuthContext ctx, string? selfCompanyId = null)
    {
        var accessible = AccessibleCompanies(ctx, selfCompanyId);
        if (accessible is null) return query; // wildcard ⇒ no filter (WhereIn null convention)
        return query.Where(x => accessible.Contains(x.CompanyID) || accessible.Contains(x.IntermediaryCompanyID));
    }

    /// <summary>
    /// The legacy row path for the Company dimension (<c>HasDefaultDataLevelAccess</c>): a single-column
    /// <c>Can(Companies, access, CompanyID, self)</c> at the operation's level. When
    /// <paramref name="disableCompanyFilter"/> is <see langword="true"/> the dimension is skipped entirely and the
    /// check passes unconditionally — exactly as the production flag behaves.
    /// </summary>
    public static bool RowCheck(Vehicle entity, TypeAuthContext ctx, Access access, bool disableCompanyFilter, string? selfCompanyId = null)
    {
        if (!disableCompanyFilter)
        {
            // Vehicle is IEntityHasCompany<T> in production; here we read CompanyID directly.
            // A null FK is passed as null and mapped to EmptyOrNullKey by Can(...), as in production.
            var encodedCompanyId = entity.CompanyID is null ? null : entity.CompanyID.Value.ToString();
            if (!ctx.Can(VehicleDataLevel.Companies, access, encodedCompanyId, Self(selfCompanyId)))
                return false;
        }

        return true;
    }

    private static string[]? Self(string? selfCompanyId) => selfCompanyId is null ? null : new[] { selfCompanyId };
}
