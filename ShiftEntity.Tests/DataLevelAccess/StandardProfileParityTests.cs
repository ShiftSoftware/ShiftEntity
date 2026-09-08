using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.DataLevelAccess;
using ShiftSoftware.ShiftEntity.Tests.DataLevelAccess.Scenario;
using ShiftSoftware.ShiftEntity.Web.Services;
using ShiftSoftware.ShiftIdentity.Core;
using ShiftSoftware.ShiftIdentity.Core.DTOs.Brand;
using ShiftSoftware.ShiftIdentity.Core.DTOs.City;
using ShiftSoftware.ShiftIdentity.Core.DTOs.Company;
using ShiftSoftware.ShiftIdentity.Core.DTOs.CompanyBranch;
using ShiftSoftware.ShiftIdentity.Core.DTOs.Country;
using ShiftSoftware.ShiftIdentity.Core.DTOs.Region;
using ShiftSoftware.ShiftIdentity.Core.DTOs.Team;
using ShiftSoftware.TypeAuth.Core;
using ShiftSoftware.TypeAuth.Core.Actions;
using Xunit;
using Constants = ShiftSoftware.ShiftEntity.Core.Constants; // the claim constants legacy's ClaimsPrincipalExtensions read

namespace ShiftSoftware.ShiftEntity.Tests.DataLevelAccess;

/// <summary>
/// Phase 4 — the standard profile at parity with legacy, <b>table-driven over the migrated dimensions</b> (the
/// seven dimensions differ only in marker, action, DTO type-key, self claim, and flag — so each new slice adds a
/// <see cref="DimensionSpec"/> row plus its profile block, and every parity theory runs for it). Both arms run over
/// identical inputs — the same scoped <see cref="TypeAuthContext"/> on the <em>real</em>
/// <c>ShiftIdentityActions.DataLevelAccess.*</c> action, the same principal, the same hashid service — and must
/// agree row-for-row and verdict-for-verdict:
/// <list type="bullet">
/// <item><b>legacy arm</b> — the real <see cref="DefaultDataLevelAccess"/> (execution fidelity; 0.3's
/// <see cref="LegacyDataLevelMechanism"/> was a reproduction, this is the genuine article), query path
/// (<c>ApplyDefaultDataLevelFilters</c>) + row path (<c>HasDefaultDataLevelAccess</c>);</item>
/// <item><b>profile arm</b> — <see cref="StandardDataLevelAccessProfile.AddStandardDimensions"/> compiled into a
/// <see cref="DataLevelAccessPolicy{TEntity}"/>, query path (<c>ApplyQueryFilter</c> at Read — legacy's query path
/// always uses <c>GetReadableItems</c>) + row path (<c>Authorize</c> at the operation's level).</item>
/// </list>
/// Each theory isolates one dimension by disabling every other dimension's legacy flag (the multi-marker
/// <see cref="StandardScopedEntity"/> carries them all); the AND-composition fact then proves dimensions still
/// conjoin identically. Key cases anchor the expected outcome too (not just arm-equality), so "both arms wrong the
/// same way" cannot pass silently. The row grids check every sample entity at Read, Write, and Delete.
/// </summary>
public class StandardProfileParityTests
{
    // ─── The dimension table — one row per migrated dimension (grows a row per Phase 4 slice) ───────────────────

    private sealed record DimensionSpec(
        string Name,
        string ActionField,                              // the JSON node under DataLevelAccess (= the field name)
        DynamicReadWriteDeleteAction Action,
        Type DtoType,
        string? SelfClaimType,
        bool UsesAllSelfClaims,
        Action<DefaultDataLevelAccessOptions, bool> SetDisabled,
        Action<StandardScopedEntity, long?> SetKey);

    private static readonly Dictionary<string, DimensionSpec> SpecsByName = new()
    {
        ["Country"] = new(
            "Country",
            nameof(ShiftIdentityActions.DataLevelAccess.Countries),
            ShiftIdentityActions.DataLevelAccess.Countries,
            typeof(CountryDTO),
            Constants.CountryIdClaim,
            false,
            (options, value) => options.DisableDefaultCountryFilter = value,
            (entity, value) => entity.CountryID = value),

        ["Region"] = new(
            "Region",
            nameof(ShiftIdentityActions.DataLevelAccess.Regions),
            ShiftIdentityActions.DataLevelAccess.Regions,
            typeof(RegionDTO),
            Constants.RegionIdClaim,
            false,
            (options, value) => options.DisableDefaultRegionFilter = value,
            (entity, value) => entity.RegionID = value),

        ["Company"] = new(
            "Company",
            nameof(ShiftIdentityActions.DataLevelAccess.Companies),
            ShiftIdentityActions.DataLevelAccess.Companies,
            typeof(CompanyDTO),
            Constants.CompanyIdClaim,
            false,
            (options, value) => options.DisableDefaultCompanyFilter = value,
            (entity, value) => entity.CompanyID = value),

        // The action is `Branches` while the marker/flag/claim say `CompanyBranch` — legacy's asymmetry, inherited.
        ["Branch"] = new(
            "Branch",
            nameof(ShiftIdentityActions.DataLevelAccess.Branches),
            ShiftIdentityActions.DataLevelAccess.Branches,
            typeof(CompanyBranchDTO),
            Constants.CompanyBranchIdClaim,
            false,
            (options, value) => options.DisableDefaultCompanyBranchFilter = value,
            (entity, value) => entity.CompanyBranchID = value),

        ["Brand"] = new(
            "Brand",
            nameof(ShiftIdentityActions.DataLevelAccess.Brands),
            ShiftIdentityActions.DataLevelAccess.Brands,
            typeof(BrandDTO),
            null, // Brand has no self claim in the legacy implementation.
            false,
            (options, value) => options.DisableDefaultBrandFilter = value,
            (entity, value) => entity.BrandID = value),

        ["City"] = new(
            "City",
            nameof(ShiftIdentityActions.DataLevelAccess.Cities),
            ShiftIdentityActions.DataLevelAccess.Cities,
            typeof(CityDTO),
            Constants.CityIdClaim,
            false,
            (options, value) => options.DisableDefaultCityFilter = value,
            (entity, value) => entity.CityID = value),

        ["Team"] = new(
            "Team",
            nameof(ShiftIdentityActions.DataLevelAccess.Teams),
            ShiftIdentityActions.DataLevelAccess.Teams,
            typeof(TeamDTO),
            Constants.TeamIdsClaim,
            true,
            (options, value) => options.DisableDefaultTeamFilter = value,
            (entity, value) => entity.TeamID = value),
    };

    public static TheoryData<string> Dimensions()
    {
        var data = new TheoryData<string>();
        foreach (var name in SpecsByName.Keys)
            data.Add(name);
        return data;
    }

    public static TheoryData<string> DimensionsWithSelf()
    {
        var data = new TheoryData<string>();
        foreach (var spec in SpecsByName.Values.Where(spec => spec.SelfClaimType is not null))
            data.Add(spec.Name);
        return data;
    }

    /// <summary>Every legacy dimension flag disabled — the per-dimension isolation baseline.</summary>
    private static DefaultDataLevelAccessOptions AllDisabled() => new()
    {
        DisableDefaultCountryFilter = true,
        DisableDefaultRegionFilter = true,
        DisableDefaultCompanyFilter = true,
        DisableDefaultCompanyBranchFilter = true,
        DisableDefaultBrandFilter = true,
        DisableDefaultCityFilter = true,
        DisableDefaultTeamFilter = true,
    };

    /// <summary>Options isolating <paramref name="spec"/>: its own flag enabled, every other dimension disabled.</summary>
    private static DefaultDataLevelAccessOptions OptionsForOnly(DimensionSpec spec)
    {
        var options = AllDisabled();
        spec.SetDisabled(options, false);
        return options;
    }

    // ─── Both arms over identical inputs ─────────────────────────────────────────────────────────────────────────

    private sealed record Arms(
        DefaultDataLevelAccess Legacy,
        DataLevelAccessPolicy<StandardScopedEntity> Profile,
        DataLevelAccessContext Context,
        DefaultDataLevelAccessOptions Options,
        RecordingHashIdService HashIds,
        List<StandardScopedEntity> Samples);

    private static Arms Build(
        DimensionSpec spec,
        ITypeAuthService typeAuth,
        FakeCurrentUserProvider? user = null,
        RecordingHashIdService? hashIds = null,
        DefaultDataLevelAccessOptions? options = null,
        List<StandardScopedEntity>? samples = null)
    {
        user ??= FakeCurrentUserProvider.Anonymous();
        hashIds ??= new RecordingHashIdService();
        options ??= OptionsForOnly(spec);
        samples ??= StandardScopedEntity.Samples(spec.SetKey);

        var legacy = new DefaultDataLevelAccess(typeAuth, new IdentityClaimProvider(user, hashIds), hashIds);

        var access = new DataLevelAccessBuilder<StandardScopedEntity>();
        access.AddStandardDimensions(options);
        var profile = new DataLevelAccessPolicy<StandardScopedEntity>(access);
        var context = new DataLevelAccessContext(new TypeAuthAccessibleItemsSource(typeAuth), user, hashIds);

        return new(legacy, profile, context, options, hashIds, samples);
    }

    private static List<long> LegacyVisible(Arms arms)
        => arms.Legacy.ApplyDefaultDataLevelFilters(arms.Options, arms.Samples.AsQueryable())
            .Select(x => x.ID).OrderBy(x => x).ToList();

    private static List<long> ProfileVisible(Arms arms)
        => arms.Profile.ApplyQueryFilter(arms.Samples.AsQueryable(), Access.Read, arms.Context)
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
        foreach (var entity in arms.Samples)
            foreach (var access in new[] { Access.Read, Access.Write, Access.Delete })
                Assert.Equal(
                    arms.Legacy.HasDefaultDataLevelAccess(arms.Options, entity, access),
                    arms.Profile.Authorize(entity, access, arms.Context));
    }

    private static bool LegacyRow(Arms arms, long id, Access access)
        => arms.Legacy.HasDefaultDataLevelAccess(arms.Options, arms.Samples.Single(x => x.ID == id), access);

    // ─── Per-dimension parity theories ───────────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Dimensions))]
    public void ScopedGrant_QueryAndRowParity(string dimension)
    {
        // The bread-and-butter case: a grant on id 4 must surface exactly the id-4 row through both arms, deny the
        // others (including the null-FK row), and pass the full R/W/D row grid identically.
        var spec = SpecsByName[dimension];
        var arms = Build(spec, IdentityScopedTypeAuth.ToId(spec.ActionField, 4));

        AssertQueryParity(arms, 3);
        AssertRowParity(arms);
        Assert.True(LegacyRow(arms, 3, Access.Read));  // anchors: in-scope passes…
        Assert.False(LegacyRow(arms, 1, Access.Read)); // …out-of-scope denies (so the grid isn't vacuously equal)
    }

    [Theory]
    [MemberData(nameof(Dimensions))]
    public void Wildcard_QueryAndRowParity(string dimension)
    {
        var spec = SpecsByName[dimension];
        var arms = Build(spec, IdentityScopedTypeAuth.Wildcard(spec.ActionField));

        AssertQueryParity(arms, 1, 2, 3, 4); // wildcard sees everything, the null-FK row included
        AssertRowParity(arms);
        Assert.True(LegacyRow(arms, 1, Access.Delete));
    }

    [Theory]
    [MemberData(nameof(Dimensions))]
    public void NoAccess_QueryAndRowParity(string dimension)
    {
        var spec = SpecsByName[dimension];
        var arms = Build(spec, IdentityScopedTypeAuth.None());

        AssertQueryParity(arms /* nothing */);
        AssertRowParity(arms);
        Assert.False(LegacyRow(arms, 3, Access.Read));
    }

    [Theory]
    [MemberData(nameof(Dimensions))]
    public void ReadOnlyGrant_LevelPerOperation_Parity(string dimension)
    {
        // The level mapping must agree arm-for-arm: a Read-only grant Views the id-4 row but gates Write/Delete.
        var spec = SpecsByName[dimension];
        var arms = Build(spec, IdentityScopedTypeAuth.ToId(spec.ActionField, 4, Access.Read));

        AssertQueryParity(arms, 3); // both query paths filter at Read
        AssertRowParity(arms);
        Assert.True(LegacyRow(arms, 3, Access.Read));
        Assert.False(LegacyRow(arms, 3, Access.Write));
        Assert.False(LegacyRow(arms, 3, Access.Delete));
    }

    [Theory]
    [MemberData(nameof(DimensionsWithSelf))]
    public void SelfReferenceGrant_ResolvesTheClaim_Parity(string dimension)
    {
        // A self-reference grant + the caller's dimension claim (the hashed-id claim legacy reads via
        // IdentityClaimProvider, v2 via Self(claim)) must fold the caller's own id into the accessible set
        // identically: the caller sees their own row and nothing else.
        var spec = SpecsByName[dimension];
        var arms = Build(
            spec,
            IdentityScopedTypeAuth.Self(spec.ActionField),
            FakeCurrentUserProvider.WithClaims((spec.SelfClaimType!, "4")));

        AssertQueryParity(arms, 3);
        AssertRowParity(arms);
        Assert.True(LegacyRow(arms, 3, Access.Write));
        Assert.False(LegacyRow(arms, 1, Access.Read));
    }

    [Theory]
    [MemberData(nameof(DimensionsWithSelf))]
    public void SelfReferenceGrant_CallerWithoutTheClaim_ProfileFailsClosedBeforeDecoding(string dimension)
    {
        // Claims on an UNAUTHENTICATED principal grant nothing (legacy's GetClaimValues requires IsAuthenticated;
        // v2's GetClaim matches — the 4.1 alignment). But the two arms get there very differently, and this
        // characterizes both — a legacy defect found by running the REAL DefaultDataLevelAccess:
        //   • Legacy: the missing scalar claim reaches the decoder as null; Team's null params array leaves the
        //     self-reference sentinel in the result. What a production decoder does with either unresolved value is
        //     configuration-dependent (throw, decode to 0, etc.), so this test pins the mechanism rather than a
        //     particular exception type.
        //   • Profile (v2): the absent claim resolves to "no self ids", the self reference folds to nothing, and
        //     the caller is cleanly denied everything before the hash decoder is ever invoked.
        var spec = SpecsByName[dimension];
        var hashIds = new RecordingHashIdService(
            decode: (key, _) => throw new InvalidOperationException(
                $"Legacy passed an unresolved self value to the decoder: {key ?? "<null>"}"));
        var arms = Build(
            spec,
            IdentityScopedTypeAuth.Self(spec.ActionField),
            FakeCurrentUserProvider.WithUnauthenticatedClaims((spec.SelfClaimType!, "4")),
            hashIds);

        Assert.Empty(ProfileVisible(arms));
        Assert.Empty(hashIds.DecodeCalls);

        Assert.Throws<InvalidOperationException>(() => LegacyVisible(arms));
        var legacyDecode = Assert.Single(hashIds.DecodeCalls);
        if (dimension == "Team")
            Assert.Equal(TypeAuthContext.SelfReferenceKey, legacyDecode.Key);
        else
            Assert.Null(legacyDecode.Key);

        // The row paths don't crash (no id decoding) — both arms deny everything, including the caller's "own"
        // row, at every level.
        AssertRowParity(arms);
        Assert.False(LegacyRow(arms, 3, Access.Read));
    }

    [Fact]
    public void BrandSelfReferenceGrant_NoSelfClaim_DeniesCleanlyOnBothPaths()
    {
        // Brand deliberately has no Self(...) wiring in either implementation. A self-reference grant therefore
        // resolves to no ids rather than to a caller claim (and, unlike the claim-backed dimensions, legacy does
        // not receive a null self-id that later crashes during decoding).
        var spec = SpecsByName["Brand"];
        var arms = Build(spec, IdentityScopedTypeAuth.Self(spec.ActionField));

        Assert.Null(spec.SelfClaimType);
        AssertQueryParity(arms);
        AssertRowParity(arms);
        Assert.False(LegacyRow(arms, 3, Access.Read));
    }

    [Fact]
    public void TeamSelfReferenceGrant_AllCallerTeamsBecomeAccessible_Parity()
    {
        // TeamIds is a repeated claim. Both legacy and v2 must pass every value to TypeAuth: a caller in teams 2
        // and 4 sees both rows, not just whichever claim happened to be emitted first.
        var spec = SpecsByName["Team"];
        var arms = Build(
            spec,
            IdentityScopedTypeAuth.Self(spec.ActionField),
            FakeCurrentUserProvider.WithClaims(
                (Constants.TeamIdsClaim, "2"),
                (Constants.TeamIdsClaim, "4")));

        AssertQueryParity(arms, 2, 3);
        AssertRowParity(arms);
        Assert.True(LegacyRow(arms, 2, Access.Read));
        Assert.True(LegacyRow(arms, 3, Access.Write));
        Assert.False(LegacyRow(arms, 1, Access.Read));
    }

    [Fact]
    public void ScalarSelfReferenceGrant_OnlyFirstClaimBecomesAccessible_Parity()
    {
        // Company is single-valued. Even if a malformed principal carries duplicate claims, both implementations
        // use only the first value; treating ordinary Self(...) as multi-valued would silently widen access.
        var spec = SpecsByName["Company"];
        var arms = Build(
            spec,
            IdentityScopedTypeAuth.Self(spec.ActionField),
            FakeCurrentUserProvider.WithClaims(
                (Constants.CompanyIdClaim, "2"),
                (Constants.CompanyIdClaim, "4")));

        AssertQueryParity(arms, 2);
        AssertRowParity(arms);
        Assert.True(LegacyRow(arms, 2, Access.Read));
        Assert.False(LegacyRow(arms, 3, Access.Read));
    }

    [Theory]
    [MemberData(nameof(Dimensions))]
    public void NullKeyGrant_MatchesOnlyTheNullFkRow_Parity(string dimension)
    {
        // The EmptyOrNullKey grant is the explicit "rows with no <dimension>" permission: both arms must surface
        // exactly the null-FK row — the convention defect #3 demanded be identical on both paths.
        var spec = SpecsByName[dimension];
        var arms = Build(spec, IdentityScopedTypeAuth.NullKey(spec.ActionField));

        AssertQueryParity(arms, 4);
        AssertRowParity(arms);
        Assert.True(LegacyRow(arms, 4, Access.Read));
        Assert.False(LegacyRow(arms, 1, Access.Read));
    }

    [Theory]
    [MemberData(nameof(Dimensions))]
    public void HashIdGrants_BothArmsRouteThroughTheDimensionTypeKey(string dimension)
    {
        // Grants stored hashid-encoded ("K4") must decode through the dimension's DTO type-key on BOTH arms —
        // legacy via Decode<TDto>/Encode<TDto>, the profile via HashId<TDto>() — landing on id 4. A raw
        // long.Parse("K4") would throw, so green proves the routing; the recorded DTO types prove the type-key.
        var spec = SpecsByName[dimension];
        var hashIds = new RecordingHashIdService(
            decode: (key, _) => key == "K4" ? 4 : long.Parse(key),
            encode: (id, _) => id == 4 ? "K4" : id.ToString());
        var arms = Build(spec, IdentityScopedTypeAuth.ToKeys(spec.ActionField, new[] { "K4" }), hashIds: hashIds);

        AssertQueryParity(arms, 3);
        AssertRowParity(arms);

        Assert.NotEmpty(hashIds.DecodeCalls);
        Assert.All(hashIds.DecodeCalls, call => Assert.Equal(spec.DtoType, call.DtoType));
        Assert.NotEmpty(hashIds.EncodeCalls); // the legacy row path encodes the entity's id before Can(...)
        Assert.All(hashIds.EncodeCalls, call => Assert.Equal(spec.DtoType, call.DtoType));
    }

    [Theory]
    [MemberData(nameof(Dimensions))]
    public void FlagDisabled_LegacyFiltersNothing_ProfileDeclaresNothing(string dimension)
    {
        // The legacy flag semantics carry over 1:1: with the dimension's Disable flag the legacy arm applies no
        // filter and passes every row — and the profile declares no dimension at all (same meaning, declaration
        // level). An all-disabled declaration is empty, and compiling an empty declaration throws by design (the
        // 3.3 fail-closed guard) — pinning what the future auto-wiring must respect.
        var spec = SpecsByName[dimension];
        var typeAuth = IdentityScopedTypeAuth.ToId(spec.ActionField, 4);
        var hashIds = new RecordingHashIdService();
        var user = FakeCurrentUserProvider.Anonymous();
        var options = AllDisabled(); // including the dimension under test
        var samples = StandardScopedEntity.Samples(spec.SetKey);

        var legacy = new DefaultDataLevelAccess(typeAuth, new IdentityClaimProvider(user, hashIds), hashIds);
        Assert.Equal(
            new long[] { 1, 2, 3, 4 },
            legacy.ApplyDefaultDataLevelFilters(options, samples.AsQueryable())
                .Select(x => x.ID).OrderBy(x => x).ToList());
        Assert.True(legacy.HasDefaultDataLevelAccess(options, samples[0], Access.Write));

        var access = new DataLevelAccessBuilder<StandardScopedEntity>();
        access.AddStandardDimensions(options);
        Assert.Empty(access.Dimensions);
        Assert.Throws<InvalidOperationException>(() => new DataLevelAccessPolicy<StandardScopedEntity>(access));
    }

    [Theory]
    [MemberData(nameof(Dimensions))]
    public void DeclaredDimension_CarriesTheLegacyWiringExactly(string dimension)
    {
        // The recorded shape IS the parity contract: the real action (same singleton), the dimension's hashid DTO
        // type-key, the dimension's id claim as Self, and a single key column.
        var spec = SpecsByName[dimension];
        var access = new DataLevelAccessBuilder<StandardScopedEntity>();
        access.AddStandardDimensions(OptionsForOnly(spec));

        var declared = Assert.Single(access.Dimensions);
        var source = Assert.IsType<TypeAuthValueSource>(declared.ValueSource);
        Assert.Same(spec.Action, source.Action);
        Assert.Equal(spec.DtoType, declared.HashIdDtoType);
        Assert.Equal(spec.SelfClaimType, declared.SelfClaimType);
        Assert.Equal(spec.UsesAllSelfClaims, declared.UsesAllSelfClaims);
        var keys = Assert.IsType<KeysPredicate<StandardScopedEntity>>(declared.Predicate);
        Assert.Single(keys.Selectors);
    }

    // ─── Cross-dimension facts ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EntityWithoutTheMarkers_ProfileDeclaresNothing()
    {
        // No markers ⇒ no dimensions, mirroring legacy (which filters nothing for an unmarked entity).
        var access = new DataLevelAccessBuilder<UnmarkedEntity>();
        access.AddStandardDimensions(new DefaultDataLevelAccessOptions());

        Assert.Empty(access.Dimensions);
    }

    private class UnmarkedEntity : ShiftEntity<UnmarkedEntity> { }

    [Fact]
    public void TwoDimensions_AndCompose_Parity()
    {
        // Dimensions conjoin: with Country AND Company enabled (grants: country 4, company 4), a row is visible
        // only when BOTH columns are in scope — one out-of-scope or null leg hides it. Both arms must agree on
        // every combination, query and row grid alike (the isolation theories above can't see composition).
        var options = AllDisabled();
        options.DisableDefaultCountryFilter = false;
        options.DisableDefaultCompanyFilter = false;

        var samples = new List<StandardScopedEntity>
        {
            new() { ID = 1, Name = "in/in",     CountryID = 4,    CompanyID = 4 },
            new() { ID = 2, Name = "in/out",    CountryID = 4,    CompanyID = 1 },
            new() { ID = 3, Name = "out/in",    CountryID = 1,    CompanyID = 4 },
            new() { ID = 4, Name = "null/in",   CountryID = null, CompanyID = 4 },
            new() { ID = 5, Name = "in/null",   CountryID = 4,    CompanyID = null },
        };

        var typeAuth = IdentityScopedTypeAuth.ToIdsOnActions(new Dictionary<string, IEnumerable<long>>
        {
            [nameof(ShiftIdentityActions.DataLevelAccess.Countries)] = new long[] { 4 },
            [nameof(ShiftIdentityActions.DataLevelAccess.Companies)] = new long[] { 4 },
        });

        var arms = Build(SpecsByName["Country"], typeAuth, options: options, samples: samples);

        AssertQueryParity(arms, 1); // only the fully in-scope row survives the conjunction
        AssertRowParity(arms);
        Assert.True(LegacyRow(arms, 1, Access.Read));
        Assert.False(LegacyRow(arms, 2, Access.Read)); // the in/out row: Country passes, Company denies
    }
}
