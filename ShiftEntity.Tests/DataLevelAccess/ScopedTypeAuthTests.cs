using ShiftSoftware.ShiftEntity.Tests.DataLevelAccess.Scenario;
using ShiftSoftware.TypeAuth.Core;
using ShiftSoftware.TypeAuth.Core.Linq;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.DataLevelAccess;

/// <summary>
/// Slice 0.2 — proves the scoped <see cref="TypeAuthContext"/> helper builds contexts that behave
/// correctly (none / wildcard / id-set / self / null-company) over the in-memory Vehicle scenario.
/// This is the foundation the 0.3 characterization (the cross-column OR gap) and the Phase 2 engine build on.
/// </summary>
public class ScopedTypeAuthTests
{
    private static IQueryable<Vehicle> Vehicles() => VehicleScenario.SampleVehicles().AsQueryable();

    private static List<Vehicle> VisibleByCompanyId(TypeAuthContext ctx, params string[] selfId)
    {
        var readable = ctx.GetReadableItems(ScopedTypeAuth.CompaniesAction, selfId);
        return Vehicles().WhereAccessible(readable, x => x.CompanyID, ScopedTypeAuth.ToCompanyId).ToList();
    }

    [Fact]
    public void None_SeesNothing()
    {
        var ctx = ScopedTypeAuth.None();
        var readable = ctx.GetReadableItems(ScopedTypeAuth.CompaniesAction);

        Assert.False(readable.WildCard);
        Assert.Empty(readable.AccessibleIds);
        Assert.Empty(VisibleByCompanyId(ctx));
    }

    [Fact]
    public void Wildcard_SeesEverything()
    {
        var ctx = ScopedTypeAuth.Wildcard();
        var readable = ctx.GetReadableItems(ScopedTypeAuth.CompaniesAction);

        Assert.True(readable.WildCard);
        Assert.Equal(VehicleScenario.SampleVehicles().Count, VisibleByCompanyId(ctx).Count);
    }

    [Fact]
    public void ToCompany_ScopesToThatId()
    {
        var ctx = ScopedTypeAuth.ToCompany(Companies.Intermediary);
        var readable = ctx.GetReadableItems(ScopedTypeAuth.CompaniesAction);

        Assert.False(readable.WildCard);
        Assert.Equal(new[] { "4" }, readable.AccessibleIds);

        // Single-column CompanyID view sees only the Intermediary-owned vehicle (#3) — the
        // intermediary-leg vehicles (#4/#5/#6) are invisible here. Closing that gap is the whole
        // point of v2 (slice 0.3+).
        Assert.Equal(new long?[] { Companies.Intermediary }, VisibleByCompanyId(ctx).Select(v => v.CompanyID).ToArray());
    }

    [Fact]
    public void Self_ResolvesToTheSuppliedCompany()
    {
        var ctx = ScopedTypeAuth.Self();

        // "self" resolves to whatever current-company id is supplied at evaluation time.
        var asIntermediary = ctx.GetReadableItems(ScopedTypeAuth.CompaniesAction, Companies.Intermediary.ToString());
        Assert.Equal(new[] { "4" }, asIntermediary.AccessibleIds);

        var asDealerA = ctx.GetReadableItems(ScopedTypeAuth.CompaniesAction, Companies.DealerA.ToString());
        Assert.Equal(new[] { "1" }, asDealerA.AccessibleIds);
    }

    [Fact]
    public void NullCompany_MatchesNullForeignKeys()
    {
        var ctx = ScopedTypeAuth.NullCompany();
        var readable = ctx.GetReadableItems(ScopedTypeAuth.CompaniesAction);
        Assert.Contains(TypeAuthContext.EmptyOrNullKey, readable.AccessibleIds);

        // ConvertIds maps the sentinel to null, so only null-CompanyID rows (#6, #7) match.
        var visible = VisibleByCompanyId(ctx);
        Assert.NotEmpty(visible);
        Assert.All(visible, v => Assert.Null(v.CompanyID));
    }

    [Fact]
    public void AccessLevels_AreTrackedSeparately()
    {
        // Granting only Read on company 4 must not leak into Write/Delete.
        var byAccess = ScopedTypeAuth
            .ToCompany(Companies.Intermediary, Access.Read)
            .GetAccessibleItemsByAccess(ScopedTypeAuth.CompaniesAction);

        Assert.Equal(new[] { "4" }, byAccess.Read.AccessibleIds);
        Assert.Empty(byAccess.Write.AccessibleIds);
        Assert.Empty(byAccess.Delete.AccessibleIds);
    }
}
