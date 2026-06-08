using ShiftSoftware.ShiftEntity.Tests.DataLevelAccess.Scenario;
using ShiftSoftware.TypeAuth.Core;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.DataLevelAccess;

/// <summary>
/// Slice 0.3 — a GOLDEN MASTER of <b>today's (broken)</b> data-level behavior for the cross-column OR case,
/// captured before v2 changes it. Every assertion here documents a <i>current limitation</i>, not a desired
/// outcome; Phase 4 reproduces the same scenario on the v2 engine and FLIPS these to the fixed behavior.
/// <para>
/// It exercises the mechanism (<see cref="LegacyDataLevelMechanism"/>) over the slice-0.2 scenario — no DB, no
/// ShiftIdentity, and it does not wire <c>DefaultDataLevelAccess</c> (per the slice brief).
/// </para>
/// <para>
/// Scenario: the <b>Intermediary</b> user (company 4) over <see cref="VehicleScenario.SampleVehicles"/>. The rule
/// they NEED is "<c>CompanyID == 4 OR IntermediaryCompanyID == 4</c>" = vehicles {3, 4, 5, 6}. Today's
/// single-column tools cannot satisfy it on both paths at once:
/// <list type="bullet">
/// <item>keep the Company filter <b>ON</b> ⇒ reads are too restrictive (only {3}) and can't be widened (AND-trap);</item>
/// <item>turn the Company filter <b>OFF</b> ⇒ reads can be made correct by a hand-written OR, but the row path
/// honors the same flag and stops guarding writes (the security hole).</item>
/// </list>
/// No single configuration is correct on both paths — that is the expressiveness gap v2 closes.
/// </para>
/// </summary>
public class LegacyCharacterizationTests
{
    private static IQueryable<Vehicle> Vehicles() => VehicleScenario.SampleVehicles().AsQueryable();

    private static TypeAuthContext Intermediary() => ScopedTypeAuth.ToCompany(Companies.Intermediary);

    /// <summary>The caller's own company id, passed as the self-claim exactly as production does.</summary>
    private static readonly string IntermediarySelf = Companies.Intermediary.ToString();

    /// <summary>The OR the Intermediary actually needs — vehicles on either leg (CompanyID==4 OR Intermediary==4).</summary>
    private static readonly long[] WantedOrIds = { 3, 4, 5, 6 };

    private static long[] Ids(IEnumerable<Vehicle> vehicles) => vehicles.Select(v => v.Id).OrderBy(id => id).ToArray();

    // ───────────────────────── Query path (reads) ─────────────────────────

    [Fact]
    public void QueryPath_SingleColumnCompany_IsTooRestrictive_MissesIntermediaryLeg()
    {
        var ctx = Intermediary();

        // Faithful to GetAccessibleCompanies(): the Read-accessible company set is exactly {4}.
        var accessible = LegacyDataLevelMechanism.AccessibleCompanies(ctx, IntermediarySelf);
        Assert.Equal(new long?[] { Companies.Intermediary }, accessible);

        var visible = LegacyDataLevelMechanism.ApplyCompanyQueryFilter(Vehicles(), ctx, IntermediarySelf).ToList();

        // TODAY: only the Intermediary-OWNED vehicle (#3, CompanyID==4) is visible …
        Assert.Equal(new long[] { 3 }, Ids(visible));

        // … the intermediary-LEG vehicles (#4/#5/#6, IntermediaryCompanyID==4) are invisible — the gap.
        Assert.DoesNotContain(visible, v => v.Id == 4 || v.Id == 5 || v.Id == 6);
        Assert.NotEqual(WantedOrIds, Ids(visible)); // Phase 4 flips this to Equal(WantedOrIds, …).
    }

    [Fact]
    public void QueryPath_AddingOrFilterWhileDefaultOn_CollapsesByAnd()
    {
        var ctx = Intermediary();

        // The default single-column filter AND a hand-written OR filter (two stacked Where()s = AND).
        var singleColumn = LegacyDataLevelMechanism.ApplyCompanyQueryFilter(Vehicles(), ctx, IntermediarySelf);
        var withOrOnTop = LegacyDataLevelMechanism.ApplyCompanyOrQueryFilter(singleColumn, ctx, IntermediarySelf);

        // The OR is DEAD: CompanyID∈{4} AND (CompanyID∈{4} OR Intermediary∈{4}) collapses to CompanyID∈{4} = {3}.
        // So you cannot widen to the OR by adding a filter while the default Company filter is on.
        Assert.Equal(new long[] { 3 }, Ids(withOrOnTop.ToList()));
    }

    [Fact]
    public void QueryPath_DisablingDefaultPlusHandwrittenOr_ReadsCorrectly()
    {
        var ctx = Intermediary();

        // With the default Company filter OFF, the hand-written OR alone yields the correct read set …
        var visible = LegacyDataLevelMechanism.ApplyCompanyOrQueryFilter(Vehicles(), ctx, IntermediarySelf).ToList();

        Assert.Equal(WantedOrIds, Ids(visible));
        // … but disabling the filter is exactly what removes row-path enforcement — see
        // RowPath_DisablingCompanyToEnableOrQuery_LeavesWritesUnguarded for the cost of getting here.
    }

    // ───────────────────────── Row path (writes / deletes) ─────────────────────────

    [Fact]
    public void RowPath_CompanyDimensionOn_DeniesOutOfScope_ButAlsoDeniesLegitimateIntermediaryLeg()
    {
        var ctx = Intermediary();
        var dealerBOwned = Vehicles().Single(v => v.Id == 2);    // C=DealerB, I=null — wholly out of scope
        var intermediaryLeg = Vehicles().Single(v => v.Id == 4); // C=DealerA, I=Intermediary — SHOULD be writable

        // Correct: a wholly out-of-scope vehicle is denied for Edit (Write) and Delete.
        Assert.False(LegacyDataLevelMechanism.RowCheck(dealerBOwned, ctx, Access.Write, disableCompanyFilter: false, IntermediarySelf));
        Assert.False(LegacyDataLevelMechanism.RowCheck(dealerBOwned, ctx, Access.Delete, disableCompanyFilter: false, IntermediarySelf));

        // TODAY's flip side: the row path is single-column too, so it WRONGLY denies the intermediary-leg
        // vehicle the user legitimately handles (it only inspects CompanyID==DealerA, never Intermediary==4).
        // Phase 4 flips this to allowed.
        Assert.False(LegacyDataLevelMechanism.RowCheck(intermediaryLeg, ctx, Access.Write, disableCompanyFilter: false, IntermediarySelf));
    }

    [Fact]
    public void RowPath_DisablingCompanyToEnableOrQuery_LeavesWritesUnguarded()
    {
        var ctx = Intermediary();
        var dealerBOwned = Vehicles().Single(v => v.Id == 2); // C=DealerB, I=null — wholly out of scope for company 4

        // Sanity: with the dimension ON this vehicle is denied — the guard exists.
        Assert.False(LegacyDataLevelMechanism.RowCheck(dealerBOwned, ctx, Access.Write, disableCompanyFilter: false, IntermediarySelf));

        // THE HOLE: disabling the Company filter is the ONLY way to get the OR read query
        // (QueryPath_DisablingDefaultPlusHandwrittenOr_ReadsCorrectly), but the row path honors the SAME flag —
        // so Insert/Edit (Write) and Delete on a wholly out-of-scope vehicle are now ALLOWED. No company
        // authorization remains on writes. Phase 4 closes this by deriving both paths from one policy.
        Assert.True(LegacyDataLevelMechanism.RowCheck(dealerBOwned, ctx, Access.Write, disableCompanyFilter: true, IntermediarySelf));  // Insert + Edit
        Assert.True(LegacyDataLevelMechanism.RowCheck(dealerBOwned, ctx, Access.Delete, disableCompanyFilter: true, IntermediarySelf)); // Delete
    }
}
