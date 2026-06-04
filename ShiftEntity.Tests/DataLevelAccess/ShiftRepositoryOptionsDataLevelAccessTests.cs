using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.DataLevelAccess;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.Tests.DataLevelAccess.Scenario;
using ShiftSoftware.TypeAuth.Core;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.DataLevelAccess;

/// <summary>
/// Slice 2.5 — the consumer-facing declaration point: <c>ShiftRepositoryOptions.DataLevelAccess(...)</c> compiles the
/// declared dimensions into a <see cref="DataLevelAccessPolicy{TEntity}"/> and records it on the options. Recorded
/// only for now — <c>ShiftRepository</c> starts enforcing the stored policy in Phase 3; these tests pin down that the
/// stored policy is the <i>live</i> compiled engine (not a re-buildable description), that declaring is one-shot, and
/// that fail-closed validation fires at declaration (startup) time rather than first query.
/// </summary>
public class ShiftRepositoryOptionsDataLevelAccessTests
{
    // The ShiftEntity<T>-derived twin of the scenario POCO lives in Scenario/VehicleEntity.cs — shared with the
    // Phase 3 repository tests, which also map it through EF.
    private static IQueryable<VehicleEntity> Vehicles() => VehicleEntity.FromSamples().AsQueryable();

    private static DataLevelAccessContext IntermediaryContext()
        => new(new TypeAuthAccessibleItemsSource(ScopedTypeAuth.ToCompany(Companies.Intermediary)),
               FakeCurrentUserProvider.Anonymous(),
               new RecordingHashIdService());

    [Fact]
    public void DataLevelAccess_RecordsACompiledLivePolicy()
    {
        var options = new ShiftRepositoryOptions<VehicleEntity>();

        options.DataLevelAccess(access =>
            access.On(VehicleDataLevel.Companies).Keys(x => x.CompanyID, x => x.IntermediaryCompanyID));

        // Recorded and retrievable…
        var policy = options.DataLevelAccessPolicy;
        Assert.NotNull(policy);
        Assert.False(policy.IsUnscoped);

        // …and live: the stored policy is the compiled engine — the canonical cross-column OR filters through it
        // (Intermediary sees the owner leg #3 plus the intermediary legs #4/#5/#6), ready for Phase 3 to consume.
        var visible = policy.ApplyQueryFilter(Vehicles(), Access.Read, IntermediaryContext())
            .Select(v => v.ID).OrderBy(id => id).ToList();
        Assert.Equal(new long[] { 3, 4, 5, 6 }, visible);
    }

    [Fact]
    public void DataLevelAccessPolicy_IsNullUntilDeclared()
    {
        // No declaration ⇒ no policy — the signal Phase 3 keys "use the legacy path" off of.
        Assert.Null(new ShiftRepositoryOptions<VehicleEntity>().DataLevelAccessPolicy);
    }

    [Fact]
    public void DataLevelAccess_NullDeclaration_Throws()
    {
        var options = new ShiftRepositoryOptions<VehicleEntity>();

        Assert.Throws<ArgumentNullException>(() => options.DataLevelAccess(null!));
    }

    [Fact]
    public void DataLevelAccess_DeclaredTwice_Throws()
    {
        // One entity, one policy: a second declaration must not silently widen or replace a security declaration.
        var options = new ShiftRepositoryOptions<VehicleEntity>();
        options.DataLevelAccess(access => access.On(VehicleDataLevel.Companies).Key(x => x.CompanyID));

        Assert.Throws<InvalidOperationException>(() =>
            options.DataLevelAccess(access => access.On(VehicleDataLevel.Companies).Key(x => x.IntermediaryCompanyID)));
    }

    [Fact]
    public void DataLevelAccess_DimensionWithoutPredicate_FailsClosedAtDeclarationTime()
    {
        // A value source with no Key/Keys/Match would be an unenforceable dimension. The policy ctor validates, so
        // the declaration itself throws — at repository-construction (startup) time, not on some later query.
        var options = new ShiftRepositoryOptions<VehicleEntity>();

        Assert.Throws<InvalidOperationException>(() =>
            options.DataLevelAccess(access => access.On(VehicleDataLevel.Companies)));
    }

    [Fact]
    public void DataLevelAccess_Unscoped_RecordsAnUnscopedPolicy()
    {
        // Unscoped() is an explicit, recorded "this entity has no data-level scope" (≠ never declared) — Phase 3
        // can distinguish a deliberate opt-out from a missing declaration.
        var options = new ShiftRepositoryOptions<VehicleEntity>();

        options.DataLevelAccess(access => access.Unscoped());

        Assert.NotNull(options.DataLevelAccessPolicy);
        Assert.True(options.DataLevelAccessPolicy.IsUnscoped);
    }
}
