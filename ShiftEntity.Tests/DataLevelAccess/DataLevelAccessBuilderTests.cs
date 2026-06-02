using ShiftSoftware.ShiftEntity.Core.DataLevelAccess;
using ShiftSoftware.ShiftEntity.Tests.DataLevelAccess.Scenario;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.DataLevelAccess;

/// <summary>
/// Slice 2.2 — the v2 engine's declaration model (D11). Each dimension is two independent choices: <b>where the
/// accessible set comes from</b> (On / On+Self / OnOwner) and <b>how the entity is matched</b> against it (Key/Keys /
/// Match). These tests inspect what the builder <i>records</i>; nothing is applied to a query or row here — that's
/// slice 2.3/2.4. See <c>04-api-mental-model.md</c> in the plan for the full mental model.
/// </summary>
public class DataLevelAccessBuilderTests
{
    private sealed class FakeDto { }

    private static DataLevelAccessBuilder<Vehicle> Access() => new();

    [Fact]
    public void On_Keys_RecordsTypeAuthDimensionWithSelectors()
    {
        // The 80% case (≈ the old FilterBy): "a Vehicle is in scope if CompanyID OR IntermediaryCompanyID is one of
        // the companies my role grants me" — the set comes from the Companies TypeAuth action, matched on 2 columns.
        var access = Access();
        access.On(VehicleDataLevel.Companies).Keys(x => x.CompanyID, x => x.IntermediaryCompanyID);

        var dim = Assert.Single(access.Dimensions);
        var source = Assert.IsType<TypeAuthValueSource>(dim.ValueSource);   // source: a TypeAuth grant
        Assert.Same(VehicleDataLevel.Companies, source.Action);

        var keys = Assert.IsType<KeysPredicate<Vehicle>>(dim.Predicate);    // predicate: columns ∈ set (OR)
        Assert.Equal(2, keys.Selectors.Count);
        Assert.Null(dim.HashIdDtoType);
        Assert.Null(dim.SelfClaimType);
    }

    [Fact]
    public void On_Key_RecordsSingleSelector()
    {
        // Single-column variant: "in scope if CompanyID is one of my granted companies."
        var access = Access();
        access.On(VehicleDataLevel.Companies).Key(x => x.CompanyID);

        var dim = Assert.Single(access.Dimensions);
        var keys = Assert.IsType<KeysPredicate<Vehicle>>(dim.Predicate);
        Assert.Single(keys.Selectors);
    }

    [Fact]
    public void On_Match_RecordsMatchPredicate()
    {
        // Match is the escape hatch for predicates Keys can't express (child collections, conditions). Here it simply
        // hand-writes the same cross-column OR Keys would produce — to show Match can do what Keys does, and more.
        var access = Access();
        DataLevelMatch<Vehicle> match =
            (set, self) => v => set.Contains(v.CompanyID) || set.Contains(v.IntermediaryCompanyID);
        access.On(VehicleDataLevel.Companies).Match(match);

        var dim = Assert.Single(access.Dimensions);
        var m = Assert.IsType<MatchPredicate<Vehicle>>(dim.Predicate);
        Assert.Same(match, m.Match);   // recorded verbatim; the engine invokes it at apply time (2.3)
    }

    [Fact]
    public void On_HashIdAndSelf_AreRecorded()
    {
        // Self augments a TypeAuth grant with "my own company" (resolved from the company_id claim at apply time);
        // HashId says the ids are hashid-encoded as the given DTO (decode for the query, encode for the row check).
        var access = Access();
        access.On(VehicleDataLevel.Companies).Keys(x => x.CompanyID).HashId<FakeDto>().Self("company_id");

        var dim = Assert.Single(access.Dimensions);
        Assert.Equal(typeof(FakeDto), dim.HashIdDtoType);
        Assert.Equal("company_id", dim.SelfClaimType);
    }

    [Fact]
    public void OnOwner_RecordsOwnerClaimSource()
    {
        // OnOwner is a DIFFERENT source: the set is the caller's own id (read from the "user_id" claim), not a
        // TypeAuth grant. "A Vehicle is visible only if it's assigned to me."
        var access = Access();
        access.OnOwner("user_id").Key(x => x.AssignedUserID);

        var dim = Assert.Single(access.Dimensions);
        var source = Assert.IsType<OwnerClaimValueSource>(dim.ValueSource);
        Assert.Equal("user_id", source.ClaimType);
    }

    [Fact]
    public void Dimensions_AreRecordedInDeclarationOrder()
    {
        // Two dimensions AND-compose: scoped to my companies AND assigned to me.
        var access = Access();
        access.On(VehicleDataLevel.Companies).Keys(x => x.CompanyID, x => x.IntermediaryCompanyID);
        access.OnOwner("user_id").Key(x => x.AssignedUserID);

        Assert.Equal(2, access.Dimensions.Count);
        Assert.IsType<TypeAuthValueSource>(access.Dimensions[0].ValueSource);
        Assert.IsType<OwnerClaimValueSource>(access.Dimensions[1].ValueSource);
    }

    [Fact]
    public void Unscoped_SetsFlagAndRecordsNoDimensions()
    {
        // Explicit "this entity has no data-level scope" — documents the intent instead of leaving it implicit.
        var access = Access();
        access.Unscoped();

        Assert.True(access.IsUnscoped);
        Assert.Empty(access.Dimensions);
    }

    [Fact]
    public void Unscoped_AfterDimension_Throws()
    {
        // Unscoped and dimensions are contradictory — fail fast rather than silently pick one.
        var access = Access();
        access.On(VehicleDataLevel.Companies).Key(x => x.CompanyID);

        Assert.Throws<InvalidOperationException>(() => access.Unscoped());
    }

    [Fact]
    public void Dimension_AfterUnscoped_Throws()
    {
        var access = Access();
        access.Unscoped();

        Assert.Throws<InvalidOperationException>(() => { access.On(VehicleDataLevel.Companies); });
    }

    [Fact]
    public void DuplicatePredicate_Throws()
    {
        // One dimension = one predicate. Declaring a second (here Match after Keys) is a mistake — fail fast.
        var access = Access();
        var dimension = access.On(VehicleDataLevel.Companies).Keys(x => x.CompanyID);

        Assert.Throws<InvalidOperationException>(
            () => { dimension.Match((set, self) => v => set.Contains(v.CompanyID)); });
    }

    [Fact]
    public void Self_OnOwnerDimension_Throws()
    {
        // Self only makes sense on a TypeAuth grant (it expands the grant's self-reference). An owner dimension is
        // already "me", so Self on it is a misconfiguration.
        var access = Access();
        var dimension = access.OnOwner("user_id").Key(x => x.AssignedUserID);

        Assert.Throws<InvalidOperationException>(() => { dimension.Self("company_id"); });
    }

    [Fact]
    public void Keys_WithNoSelectors_Throws()
    {
        // A dimension matching on zero columns can't mean anything — fail fast (mirrors the OR primitive's guard).
        var access = Access();

        Assert.Throws<ArgumentException>(() => { access.On(VehicleDataLevel.Companies).Keys(); });
    }

    [Fact]
    public void Validate_PredicatelessDimension_Throws()
    {
        // Fail-closed: a dimension that declares a source but no predicate would otherwise be a silent no-op
        // (= no filter = leak). The policy calls Validate() before applying (2.3).
        var access = Access();
        access.On(VehicleDataLevel.Companies); // source declared, predicate missing

        Assert.Throws<InvalidOperationException>(() => access.Validate());
    }

    [Fact]
    public void Validate_AllDimensionsComplete_Passes()
    {
        var access = Access();
        access.On(VehicleDataLevel.Companies).Keys(x => x.CompanyID, x => x.IntermediaryCompanyID);
        access.OnOwner("user_id").Key(x => x.AssignedUserID);

        access.Validate(); // must not throw
    }
}
