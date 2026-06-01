using ShiftSoftware.ShiftEntity.Core.DataLevelAccess;
using ShiftSoftware.ShiftEntity.Tests.DataLevelAccess.Scenario;
using ShiftSoftware.TypeAuth.Core;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.DataLevelAccess;

/// <summary>
/// Slice 2.1 — the v2 engine's <see cref="IAccessibleItemsSource"/> over a real scoped <see cref="TypeAuthContext"/>
/// (the 0.2/0.3 Vehicle scenario): correct per-level sets, wildcard passthrough, and per-request memoization.
/// </summary>
public class AccessibleItemsSourceTests
{
    [Fact]
    public void GetByAccess_ReturnsPerLevelSets_FromRealContext()
    {
        // Read-only grant on the Intermediary company (4): the row path (Write/Delete) must stay empty.
        var ctx = ScopedTypeAuth.ToCompany(Companies.Intermediary, Access.Read);
        var source = new TypeAuthAccessibleItemsSource(ctx);

        var byAccess = source.GetByAccess(ScopedTypeAuth.CompaniesAction);

        Assert.Equal(new[] { "4" }, byAccess.For(Access.Read).AccessibleIds);
        Assert.Empty(byAccess.For(Access.Write).AccessibleIds);
        Assert.Empty(byAccess.For(Access.Delete).AccessibleIds);
    }

    [Fact]
    public void GetByAccess_PassesThroughWildcard()
    {
        var source = new TypeAuthAccessibleItemsSource(ScopedTypeAuth.Wildcard());

        var byAccess = source.GetByAccess(ScopedTypeAuth.CompaniesAction);

        Assert.True(byAccess.For(Access.Read).WildCard);
        Assert.True(byAccess.For(Access.Delete).WildCard);
    }

    [Fact]
    public void GetByAccess_MemoizesPerAction()
    {
        var source = new TypeAuthAccessibleItemsSource(ScopedTypeAuth.ToCompany(Companies.Intermediary));

        var first = source.GetByAccess(ScopedTypeAuth.CompaniesAction);
        var second = source.GetByAccess(ScopedTypeAuth.CompaniesAction);

        // The real context builds a fresh AccessibleItemsByAccess each call, so reference-equality proves the
        // source returned its cached instance rather than re-traversing the access tree.
        Assert.Same(first, second);
    }

    [Fact]
    public void GetByAccess_DistinguishesSelfIds()
    {
        // "Self" grants the self-reference key, resolved from the self-id supplied at evaluation time.
        var source = new TypeAuthAccessibleItemsSource(ScopedTypeAuth.Self());

        var asDealerA = source.GetByAccess(ScopedTypeAuth.CompaniesAction, Companies.DealerA.ToString());
        var asIntermediary = source.GetByAccess(ScopedTypeAuth.CompaniesAction, Companies.Intermediary.ToString());

        // Different self-ids must not share a cache entry; each resolves to its own company.
        Assert.NotSame(asDealerA, asIntermediary);
        Assert.Equal(new[] { Companies.DealerA.ToString() }, asDealerA.For(Access.Read).AccessibleIds);
        Assert.Equal(new[] { Companies.Intermediary.ToString() }, asIntermediary.For(Access.Read).AccessibleIds);
    }

    [Fact]
    public void GetByAccess_NullAction_Throws()
    {
        var source = new TypeAuthAccessibleItemsSource(ScopedTypeAuth.None());

        Assert.Throws<ArgumentNullException>(() => { source.GetByAccess(null!); });
    }
}
