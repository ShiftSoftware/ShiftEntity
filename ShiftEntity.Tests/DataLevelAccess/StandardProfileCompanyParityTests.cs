using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.DataLevelAccess;
using ShiftSoftware.ShiftEntity.Tests.DataLevelAccess.Scenario;
using ShiftSoftware.ShiftEntity.Web.Services;
using ShiftSoftware.ShiftIdentity.Core;
using ShiftSoftware.ShiftIdentity.Core.DTOs.Company;
using ShiftSoftware.TypeAuth.Core;
using Xunit;
using Constants = ShiftSoftware.ShiftEntity.Core.Constants; // the claim constants legacy's ClaimsPrincipalExtensions read

namespace ShiftSoftware.ShiftEntity.Tests.DataLevelAccess;

/// <summary>
/// Slice 4.1 — the standard profile's <b>Company</b> dimension at parity with legacy. Both arms run over identical
/// inputs — the same scoped <see cref="TypeAuthContext"/> on the <em>real</em>
/// <c>ShiftIdentityActions.DataLevelAccess.Companies</c> action, the same principal, the same hashid service — and
/// must agree row-for-row and verdict-for-verdict:
/// <list type="bullet">
/// <item><b>legacy arm</b> — the real <see cref="DefaultDataLevelAccess"/> (execution fidelity; 0.3's
/// <see cref="LegacyDataLevelMechanism"/> was a reproduction, this is the genuine article), query path
/// (<c>ApplyDefaultDataLevelFilters</c>) + row path (<c>HasDefaultDataLevelAccess</c>);</item>
/// <item><b>profile arm</b> — <see cref="StandardDataLevelAccessProfile.AddStandardDimensions"/> compiled into a
/// <see cref="DataLevelAccessPolicy{TEntity}"/>, query path (<c>ApplyQueryFilter</c> at Read — legacy's query path
/// always uses <c>GetReadableItems</c>) + row path (<c>Authorize</c> at the operation's level).</item>
/// </list>
/// Key cases anchor the expected outcome too (not just arm-equality), so "both arms wrong the same way" cannot pass
/// silently. The row grid checks every sample entity at Read, Write, and Delete.
/// </summary>
public class StandardProfileCompanyParityTests
{
    /// <summary>Both arms built over the same TypeAuth grants, principal, options, and hashid service.</summary>
    private sealed record Arms(
        DefaultDataLevelAccess Legacy,
        DataLevelAccessPolicy<CompanyScopedEntity> Profile,
        DataLevelAccessContext Context,
        DefaultDataLevelAccessOptions Options,
        RecordingHashIdService HashIds);

    private static Arms Build(
        ITypeAuthService typeAuth,
        FakeCurrentUserProvider? user = null,
        RecordingHashIdService? hashIds = null)
    {
        user ??= FakeCurrentUserProvider.Anonymous();
        hashIds ??= new RecordingHashIdService();
        var options = new DefaultDataLevelAccessOptions();

        var legacy = new DefaultDataLevelAccess(typeAuth, new IdentityClaimProvider(user, hashIds), hashIds);

        var access = new DataLevelAccessBuilder<CompanyScopedEntity>();
        access.AddStandardDimensions(options);
        var profile = new DataLevelAccessPolicy<CompanyScopedEntity>(access);
        var context = new DataLevelAccessContext(new TypeAuthAccessibleItemsSource(typeAuth), user, hashIds);

        return new(legacy, profile, context, options, hashIds);
    }

    private static List<long> LegacyVisible(Arms arms)
        => arms.Legacy.ApplyDefaultDataLevelFilters(arms.Options, CompanyScopedEntity.Samples().AsQueryable())
            .Select(x => x.ID).OrderBy(x => x).ToList();

    private static List<long> ProfileVisible(Arms arms)
        => arms.Profile.ApplyQueryFilter(CompanyScopedEntity.Samples().AsQueryable(), Access.Read, arms.Context)
            .Select(x => x.ID).OrderBy(x => x).ToList();

    /// <summary>Query parity, anchored: both arms must return exactly <paramref name="expectedIds"/>.</summary>
    private static void AssertQueryParity(Arms arms, params long[] expectedIds)
    {
        Assert.Equal(expectedIds, LegacyVisible(arms)); // anchor — pins what "correct" is
        Assert.Equal(LegacyVisible(arms), ProfileVisible(arms)); // parity proper
    }

    /// <summary>Row parity over the full grid: every sample entity at Read, Write, and Delete.</summary>
    private static void AssertRowParity(Arms arms)
    {
        foreach (var entity in CompanyScopedEntity.Samples())
            foreach (var access in new[] { Access.Read, Access.Write, Access.Delete })
                Assert.Equal(
                    arms.Legacy.HasDefaultDataLevelAccess(arms.Options, entity, access),
                    arms.Profile.Authorize(entity, access, arms.Context));
    }

    private static bool LegacyRow(Arms arms, long id, Access access)
        => arms.Legacy.HasDefaultDataLevelAccess(
            arms.Options, CompanyScopedEntity.Samples().Single(x => x.ID == id), access);

    [Fact]
    public void ScopedGrant_QueryAndRowParity()
    {
        // The bread-and-butter case: a grant on company 4 must surface exactly the company-4 row through both arms,
        // deny the others (including the null-FK row), and pass the full R/W/D row grid identically.
        var arms = Build(IdentityScopedTypeAuth.ToCompany(Companies.Intermediary));

        AssertQueryParity(arms, 3);
        AssertRowParity(arms);
        Assert.True(LegacyRow(arms, 3, Access.Read));  // anchors: in-scope passes…
        Assert.False(LegacyRow(arms, 1, Access.Read)); // …out-of-scope denies (so the grid isn't vacuously equal)
    }

    [Fact]
    public void Wildcard_QueryAndRowParity()
    {
        var arms = Build(IdentityScopedTypeAuth.Wildcard());

        AssertQueryParity(arms, 1, 2, 3, 4); // wildcard sees everything, the null-FK row included
        AssertRowParity(arms);
        Assert.True(LegacyRow(arms, 1, Access.Delete));
    }

    [Fact]
    public void NoAccess_QueryAndRowParity()
    {
        var arms = Build(IdentityScopedTypeAuth.None());

        AssertQueryParity(arms /* nothing */);
        AssertRowParity(arms);
        Assert.False(LegacyRow(arms, 3, Access.Read));
    }

    [Fact]
    public void ReadOnlyGrant_LevelPerOperation_Parity()
    {
        // The level mapping must agree arm-for-arm: a Read-only grant Views company 4's row but gates Write/Delete.
        var arms = Build(IdentityScopedTypeAuth.ToCompany(Companies.Intermediary, Access.Read));

        AssertQueryParity(arms, 3); // both query paths filter at Read
        AssertRowParity(arms);
        Assert.True(LegacyRow(arms, 3, Access.Read));
        Assert.False(LegacyRow(arms, 3, Access.Write));
        Assert.False(LegacyRow(arms, 3, Access.Delete));
    }

    [Fact]
    public void SelfReferenceGrant_ResolvesTheCompanyClaim_Parity()
    {
        // A self-reference grant + the caller's company claim (the hashed-company-id claim legacy reads via
        // IdentityClaimProvider, v2 via Self(Constants.CompanyIdClaim)) must fold the caller's own company into the
        // accessible set identically: the Intermediary employee sees their company's row and nothing else.
        var arms = Build(
            IdentityScopedTypeAuth.Self(),
            FakeCurrentUserProvider.WithClaims((Constants.CompanyIdClaim, Companies.Intermediary.ToString())));

        AssertQueryParity(arms, 3);
        AssertRowParity(arms);
        Assert.True(LegacyRow(arms, 3, Access.Write));
        Assert.False(LegacyRow(arms, 1, Access.Read));
    }

    [Fact]
    public void SelfReferenceGrant_CallerWithoutTheClaim_LegacyCrashes_ProfileFailsClosed()
    {
        // Claims on an UNAUTHENTICATED principal grant nothing (legacy's GetClaimValues requires
        // IsAuthenticated; v2's GetClaim now matches — the 4.1 alignment, closing a fail-open divergence). But the
        // two arms get there very differently, and this characterizes both — a legacy defect found by running the
        // REAL DefaultDataLevelAccess:
        //   • Legacy: the missing claim resolves selfId to null, TypeAuth resolves the self-reference grant TO that
        //     null id, and ConvertIds crashes decoding it — the query path throws ArgumentNullException (an
        //     unhandled 500). This bites ANY caller without the company claim (unauthenticated or just claim-less)
        //     whose access tree grants the Companies self reference.
        //   • Profile (v2): the absent claim resolves to "no self ids", the self reference folds to nothing, and
        //     the caller is cleanly denied everything — fail closed, no crash.
        var arms = Build(
            IdentityScopedTypeAuth.Self(),
            FakeCurrentUserProvider.WithUnauthenticatedClaims((Constants.CompanyIdClaim, Companies.Intermediary.ToString())));

        Assert.Throws<ArgumentNullException>(() => LegacyVisible(arms)); // today's behavior, pinned as 0.3 pins defects
        Assert.Empty(ProfileVisible(arms));                              // v2: no crash, no rows

        // The row paths don't crash (no id decoding) — both arms deny everything, including the caller's "own"
        // company row, at every level.
        AssertRowParity(arms);
        Assert.False(LegacyRow(arms, 3, Access.Read));
    }

    [Fact]
    public void NullCompanyGrant_MatchesOnlyTheNullFkRow_Parity()
    {
        // The EmptyOrNullKey grant is the explicit "rows with no company" permission: both arms must surface exactly
        // the null-FK row — the convention defect #3 demanded be identical on both paths.
        var arms = Build(IdentityScopedTypeAuth.NullCompany());

        AssertQueryParity(arms, 4);
        AssertRowParity(arms);
        Assert.True(LegacyRow(arms, 4, Access.Read));
        Assert.False(LegacyRow(arms, 1, Access.Read));
    }

    [Fact]
    public void HashIdGrants_BothArmsRouteThroughTheCompanyDtoTypeKey()
    {
        // Grants stored hashid-encoded ("C4") must decode through the CompanyDTO type-key on BOTH arms — legacy via
        // Decode<CompanyDTO>/Encode<CompanyDTO>, the profile via HashId<CompanyDTO>() — landing on company 4. A raw
        // long.Parse("C4") would throw, so green proves the routing; the recorded DTO types prove the type-key.
        var hashIds = new RecordingHashIdService(
            decode: (key, _) => key == "C4" ? Companies.Intermediary : long.Parse(key),
            encode: (id, _) => id == Companies.Intermediary ? "C4" : id.ToString());
        var arms = Build(IdentityScopedTypeAuth.ToCompanyKeys(new[] { "C4" }), hashIds: hashIds);

        AssertQueryParity(arms, 3);
        AssertRowParity(arms);

        Assert.NotEmpty(hashIds.DecodeCalls);
        Assert.All(hashIds.DecodeCalls, call => Assert.Equal(typeof(CompanyDTO), call.DtoType));
        Assert.NotEmpty(hashIds.EncodeCalls); // the legacy row path encodes the entity's id before Can(...)
        Assert.All(hashIds.EncodeCalls, call => Assert.Equal(typeof(CompanyDTO), call.DtoType));
    }

    [Fact]
    public void DisableDefaultCompanyFilterFlag_LegacyFiltersNothing_ProfileDeclaresNothing()
    {
        // The legacy flag semantics carry over 1:1: with DisableDefaultCompanyFilter the legacy arm applies no
        // filter and passes every row — and the profile declares no dimension at all (same meaning, declaration
        // level). An all-disabled declaration is empty, and compiling an empty declaration throws by design (the
        // 3.3 fail-closed guard) — pinning what the future auto-wiring must respect.
        var typeAuth = IdentityScopedTypeAuth.ToCompany(Companies.Intermediary);
        var hashIds = new RecordingHashIdService();
        var user = FakeCurrentUserProvider.Anonymous();
        var options = new DefaultDataLevelAccessOptions { DisableDefaultCompanyFilter = true };

        var legacy = new DefaultDataLevelAccess(typeAuth, new IdentityClaimProvider(user, hashIds), hashIds);
        Assert.Equal(
            new long[] { 1, 2, 3, 4 },
            legacy.ApplyDefaultDataLevelFilters(options, CompanyScopedEntity.Samples().AsQueryable())
                .Select(x => x.ID).OrderBy(x => x).ToList());
        Assert.True(legacy.HasDefaultDataLevelAccess(options, CompanyScopedEntity.Samples()[0], Access.Write));

        var access = new DataLevelAccessBuilder<CompanyScopedEntity>();
        access.AddStandardDimensions(options);
        Assert.Empty(access.Dimensions);
        Assert.Throws<InvalidOperationException>(() => new DataLevelAccessPolicy<CompanyScopedEntity>(access));
    }

    [Fact]
    public void EntityWithoutTheMarker_ProfileDeclaresNothing()
    {
        // No marker ⇒ no dimension, mirroring legacy (which filters nothing for an unmarked entity).
        var access = new DataLevelAccessBuilder<UnmarkedEntity>();
        access.AddStandardDimensions(new DefaultDataLevelAccessOptions());

        Assert.Empty(access.Dimensions);
    }

    private class UnmarkedEntity : ShiftEntity<UnmarkedEntity> { }

    [Fact]
    public void DeclaredDimension_CarriesTheLegacyWiringExactly()
    {
        // The recorded shape IS the parity contract: the real Companies action (same singleton), the CompanyDTO
        // hashid type-key, the company-id claim as Self, and a single key column.
        var access = new DataLevelAccessBuilder<CompanyScopedEntity>();
        access.AddStandardDimensions(new DefaultDataLevelAccessOptions());

        var dimension = Assert.Single(access.Dimensions);
        var source = Assert.IsType<TypeAuthValueSource>(dimension.ValueSource);
        Assert.Same(ShiftIdentityActions.DataLevelAccess.Companies, source.Action);
        Assert.Equal(typeof(CompanyDTO), dimension.HashIdDtoType);
        Assert.Equal(Constants.CompanyIdClaim, dimension.SelfClaimType);
        var keys = Assert.IsType<KeysPredicate<CompanyScopedEntity>>(dimension.Predicate);
        Assert.Single(keys.Selectors);
    }
}
