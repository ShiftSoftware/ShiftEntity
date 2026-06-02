using ShiftSoftware.ShiftEntity.Core.DataLevelAccess;
using ShiftSoftware.ShiftEntity.Tests.DataLevelAccess.Scenario;
using ShiftSoftware.TypeAuth.Core;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.DataLevelAccess;

/// <summary>
/// Slice 2.3 — the payoff: the declaration (2.2) applied through the source (2.1) and the variadic
/// <c>WhereAccessible</c> (1.1) actually filters a query. End-to-end over the real scoped TypeAuth context, this is
/// the same Vehicle scenario slice 0.3 locked as <i>broken</i> (single-column) — now correct (cross-column OR).
/// See <c>04-api-mental-model.md</c> in the plan for the two-axes model (source × predicate).
/// </summary>
public class DataLevelAccessPolicyTests
{
    private static IQueryable<Vehicle> Vehicles() => VehicleScenario.SampleVehicles().AsQueryable();

    // "A Vehicle is in scope if CompanyID OR IntermediaryCompanyID is one of my companies."
    private static DataLevelAccessPolicy<Vehicle> CompanyOrPolicy()
    {
        var access = new DataLevelAccessBuilder<Vehicle>();
        access.On(VehicleDataLevel.Companies).Keys(x => x.CompanyID, x => x.IntermediaryCompanyID);
        return new DataLevelAccessPolicy<Vehicle>(access);
    }

    [Fact]
    public void CrossColumnOr_IntermediarySeesBothLegs_AndStrictlyMoreThanLegacy()
    {
        // The Intermediary (company 4) — the exact scenario slice 0.3 locked as broken.
        var ctx = ScopedTypeAuth.ToCompany(Companies.Intermediary);
        var source = new TypeAuthAccessibleItemsSource(ctx);

        var v2 = CompanyOrPolicy()
            .ApplyQueryFilter(Vehicles(), Access.Read, source)
            .Select(v => v.Id).OrderBy(id => id).ToList();

        // v2 sees the owner leg (#3) AND every intermediary-leg vehicle (#4/#5/#6).
        Assert.Equal(new long[] { 3, 4, 5, 6 }, v2);

        // Legacy single-column (CompanyID only) sees just #3 — v2 sees strictly more (the closed gap).
        var legacy = LegacyDataLevelMechanism.ApplyCompanyQueryFilter(Vehicles(), ctx)
            .Select(v => v.Id).OrderBy(id => id).ToList();
        Assert.Equal(new long[] { 3 }, legacy);
        Assert.True(v2.Count > legacy.Count);
    }

    [Fact]
    public void Wildcard_SeesEveryVehicle()
    {
        var source = new TypeAuthAccessibleItemsSource(ScopedTypeAuth.Wildcard());

        var visible = CompanyOrPolicy().ApplyQueryFilter(Vehicles(), Access.Read, source).ToList();

        Assert.Equal(VehicleScenario.SampleVehicles().Count, visible.Count);
    }

    [Fact]
    public void NoAccess_SeesNothing()
    {
        var source = new TypeAuthAccessibleItemsSource(ScopedTypeAuth.None());

        var visible = CompanyOrPolicy().ApplyQueryFilter(Vehicles(), Access.Read, source).ToList();

        Assert.Empty(visible);
    }

    [Fact]
    public void TwoDimensions_AndCompose()
    {
        // Two single-column dimensions => AND: CompanyID==4 AND IntermediaryCompanyID==4. No sample vehicle has
        // both, so the result is empty — proving dimensions AND (not OR) across each other (within one dimension is OR).
        var access = new DataLevelAccessBuilder<Vehicle>();
        access.On(VehicleDataLevel.Companies).Key(x => x.CompanyID);
        access.On(VehicleDataLevel.Companies).Key(x => x.IntermediaryCompanyID);
        var policy = new DataLevelAccessPolicy<Vehicle>(access);

        var source = new TypeAuthAccessibleItemsSource(ScopedTypeAuth.ToCompany(Companies.Intermediary));
        var visible = policy.ApplyQueryFilter(Vehicles(), Access.Read, source).ToList();

        Assert.Empty(visible);
    }

    [Fact]
    public void Match_AppliesConsumerPredicate()
    {
        // Match equivalent to Key(CompanyID): visible iff CompanyID is in the accessible set.
        var access = new DataLevelAccessBuilder<Vehicle>();
        access.On(VehicleDataLevel.Companies).Match((set, self) => v => set.Contains(v.CompanyID));
        var policy = new DataLevelAccessPolicy<Vehicle>(access);

        var source = new TypeAuthAccessibleItemsSource(ScopedTypeAuth.ToCompany(Companies.Intermediary));
        var visible = policy.ApplyQueryFilter(Vehicles(), Access.Read, source).Select(v => v.Id).ToList();

        // Only the Intermediary-owned vehicle (#3) has CompanyID == 4.
        Assert.Equal(new long[] { 3 }, visible);
    }
}
