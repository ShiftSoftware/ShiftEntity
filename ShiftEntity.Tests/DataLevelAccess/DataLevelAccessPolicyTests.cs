using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.DataLevelAccess;
using ShiftSoftware.ShiftEntity.Tests.DataLevelAccess.Scenario;
using ShiftSoftware.TypeAuth.Core;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.DataLevelAccess;

/// <summary>
/// Slice 2.3 — the payoff: the declaration (2.2) applied through the source (2.1) and the variadic
/// <c>WhereIn</c>/<c>WhereAccessible</c> (1.1) actually filters a query. End-to-end over the real scoped TypeAuth
/// context, this is the same Vehicle scenario slice 0.3 locked as <i>broken</i> (single-column) — now correct
/// (cross-column OR). The follow-up (this file's later tests) wires the deferred dimension kinds — <c>Self</c>,
/// <c>OnOwner</c>, and <c>HashId</c> — through the <see cref="DataLevelAccessContext"/>. See
/// <c>04-api-mental-model.md</c> in the plan for the two-axes model (source × predicate).
/// </summary>
public class DataLevelAccessPolicyTests
{
    private static IQueryable<Vehicle> Vehicles() => VehicleScenario.SampleVehicles().AsQueryable();

    /// <summary>Bundles the scoped TypeAuth context (+ optional caller claims / hashid service) into the context the
    /// policy now takes. Defaults: no signed-in user and an identity (raw-long) hashid service — enough for the
    /// TypeAuth-only dimensions; the Self/OnOwner/HashId tests pass their own.</summary>
    private static DataLevelAccessContext Context(
        TypeAuthContext ctx, ICurrentUserProvider? user = null, IHashIdService? hashIds = null)
        => new(new TypeAuthAccessibleItemsSource(ctx),
               user ?? FakeCurrentUserProvider.Anonymous(),
               hashIds ?? new RecordingHashIdService());

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

        var v2 = CompanyOrPolicy()
            .ApplyQueryFilter(Vehicles(), Access.Read, Context(ctx))
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
        var visible = CompanyOrPolicy().ApplyQueryFilter(Vehicles(), Access.Read, Context(ScopedTypeAuth.Wildcard())).ToList();

        Assert.Equal(VehicleScenario.SampleVehicles().Count, visible.Count);
    }

    [Fact]
    public void NoAccess_SeesNothing()
    {
        var visible = CompanyOrPolicy().ApplyQueryFilter(Vehicles(), Access.Read, Context(ScopedTypeAuth.None())).ToList();

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

        var visible = policy.ApplyQueryFilter(Vehicles(), Access.Read, Context(ScopedTypeAuth.ToCompany(Companies.Intermediary))).ToList();

        Assert.Empty(visible);
    }

    [Fact]
    public void Match_AppliesConsumerPredicate()
    {
        // Match equivalent to Key(CompanyID): visible iff CompanyID is in the accessible set.
        var access = new DataLevelAccessBuilder<Vehicle>();
        access.On(VehicleDataLevel.Companies).Match(set => v => set.Contains(v.CompanyID));
        var policy = new DataLevelAccessPolicy<Vehicle>(access);

        var visible = policy.ApplyQueryFilter(Vehicles(), Access.Read, Context(ScopedTypeAuth.ToCompany(Companies.Intermediary)))
            .Select(v => v.Id).ToList();

        // Only the Intermediary-owned vehicle (#3) has CompanyID == 4.
        Assert.Equal(new long[] { 3 }, visible);
    }

    // ── Self: a TypeAuth grant augmented with the caller's own id (read from a claim) ──────────────────────────────

    [Fact]
    public void Self_FoldsCallersOwnCompanyIntoSet()
    {
        // The grant is *only* the self-reference (no explicit companies). Self("company_id") resolves it to the
        // caller's own company (claim company_id = 4), which then matches both legs exactly like ToCompany(4) would.
        var access = new DataLevelAccessBuilder<Vehicle>();
        access.On(VehicleDataLevel.Companies).Keys(x => x.CompanyID, x => x.IntermediaryCompanyID).Self("company_id");
        var policy = new DataLevelAccessPolicy<Vehicle>(access);

        var context = Context(ScopedTypeAuth.Self(), FakeCurrentUserProvider.WithClaims(("company_id", Companies.Intermediary.ToString())));
        var visible = policy.ApplyQueryFilter(Vehicles(), Access.Read, context).Select(v => v.Id).OrderBy(id => id).ToList();

        Assert.Equal(new long[] { 3, 4, 5, 6 }, visible);
    }

    [Fact]
    public void Self_AbsentClaim_SeesNothing()
    {
        // No company_id claim ⇒ the self-reference resolves to nothing ⇒ a self-only grant is empty ⇒ no rows.
        // Fail closed: a self dimension never widens to "everything" just because the caller's id is missing.
        var access = new DataLevelAccessBuilder<Vehicle>();
        access.On(VehicleDataLevel.Companies).Keys(x => x.CompanyID, x => x.IntermediaryCompanyID).Self("company_id");
        var policy = new DataLevelAccessPolicy<Vehicle>(access);

        var context = Context(ScopedTypeAuth.Self(), FakeCurrentUserProvider.Anonymous());
        var visible = policy.ApplyQueryFilter(Vehicles(), Access.Read, context).ToList();

        Assert.Empty(visible);
    }

    // ── OnOwner: the caller's own id IS the set (no TypeAuth grant) ────────────────────────────────────────────────

    private const long Alice = 10, Bob = 20;

    // The canonical SampleVehicles leave AssignedUserID null (their story is company routing); owner dimensions get
    // their own tiny, self-documenting dataset.
    private static IQueryable<Vehicle> AssignedVehicles() => new List<Vehicle>
    {
        new() { Id = 1, AssignedUserID = Alice },
        new() { Id = 2, AssignedUserID = Bob },
        new() { Id = 3, AssignedUserID = Alice },
        new() { Id = 4, AssignedUserID = null },
    }.AsQueryable();

    [Fact]
    public void OnOwner_SeesOnlyVehiclesAssignedToCaller()
    {
        // "Visible only if assigned to me." The set is the user_id claim — no TypeAuth action consulted.
        var access = new DataLevelAccessBuilder<Vehicle>();
        access.OnOwner("user_id").Key(x => x.AssignedUserID);
        var policy = new DataLevelAccessPolicy<Vehicle>(access);

        // Source is irrelevant for an owner dimension (None() here); the claim drives it.
        var context = Context(ScopedTypeAuth.None(), FakeCurrentUserProvider.WithClaims(("user_id", Alice.ToString())));
        var visible = policy.ApplyQueryFilter(AssignedVehicles(), Access.Read, context).Select(v => v.Id).OrderBy(id => id).ToList();

        Assert.Equal(new long[] { 1, 3 }, visible);
    }

    [Fact]
    public void OnOwner_AbsentClaim_SeesNothing()
    {
        // No user_id claim ⇒ empty owner set ⇒ matches nothing. Fail closed (never wildcard for an owner source).
        var access = new DataLevelAccessBuilder<Vehicle>();
        access.OnOwner("user_id").Key(x => x.AssignedUserID);
        var policy = new DataLevelAccessPolicy<Vehicle>(access);

        var context = Context(ScopedTypeAuth.None(), FakeCurrentUserProvider.Anonymous());
        var visible = policy.ApplyQueryFilter(AssignedVehicles(), Access.Read, context).ToList();

        Assert.Empty(visible);
    }

    // ── HashId: the accessible ids are hashid-encoded; the converter decodes them as the declared DTO ──────────────

    private sealed class CompanyDto { }

    [Fact]
    public void HashId_DecodesAccessibleIdsThroughHashIdService()
    {
        // The grant stores the Intermediary's company id hashid-ENCODED as "C4" (not the raw "4"). With HashId<CompanyDto>
        // the dimension decodes accessible ids via IHashIdService instead of long.Parse — so "C4" decodes to 4 and the
        // cross-column OR still lands on {3,4,5,6}. (A raw long.Parse("C4") would throw, so passing proves the routing.)
        var hashIds = new RecordingHashIdService(decode: (key, _) => key == "C4" ? 4L : long.Parse(key));

        var access = new DataLevelAccessBuilder<Vehicle>();
        access.On(VehicleDataLevel.Companies).Keys(x => x.CompanyID, x => x.IntermediaryCompanyID).HashId<CompanyDto>();
        var policy = new DataLevelAccessPolicy<Vehicle>(access);

        var context = Context(ScopedTypeAuth.ToCompanyKeys(new[] { "C4" }), hashIds: hashIds);
        var visible = policy.ApplyQueryFilter(Vehicles(), Access.Read, context).Select(v => v.Id).OrderBy(id => id).ToList();

        Assert.Equal(new long[] { 3, 4, 5, 6 }, visible);

        // The decode was routed through the hashid service as the declared DTO — not parsed as a raw long.
        Assert.NotEmpty(hashIds.DecodeCalls);
        Assert.Contains("C4", hashIds.DecodeCalls.Select(c => c.Key));
        Assert.All(hashIds.DecodeCalls, c => Assert.Equal(typeof(CompanyDto), c.DtoType));
    }
}
