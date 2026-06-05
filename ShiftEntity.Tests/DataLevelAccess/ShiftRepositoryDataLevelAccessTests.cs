using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.DataLevelAccess;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Tests.DataLevelAccess.Scenario;
using ShiftSoftware.TypeAuth.Core;
using System.Net;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.DataLevelAccess;

/// <summary>
/// Phase 3 — <c>ShiftRepository</c> routes the data-level checks: a declared v2 policy replaces the legacy behavior
/// for the entity entirely (opt-in coexistence, D1 — no declaration ⇒ today's legacy calls, byte-for-byte), an
/// explicit <c>Unscoped()</c> opts out of both paths, and a declared policy whose per-request
/// <see cref="DataLevelAccessContext"/> cannot be resolved fails closed rather than running unguarded.
/// Slice 3.1 covers the query path (<c>GetIQueryable</c> ⇒ <c>ApplyQueryFilter</c> at Read); slice 3.2 the row
/// paths (<c>FindAsync</c>/<c>UpsertAsync</c>/<c>DeleteAsync</c> ⇒ <c>Authorize</c> at the operation's level —
/// Find ⇒ Read, Insert/Edit ⇒ Write, Delete ⇒ Delete, D6). Exercised through a <em>real</em> repository over the
/// EF InMemory provider, in a DI host shaped like production (see <see cref="RepositoryHost"/>); the engine
/// semantics themselves are pinned at the policy level (2.3/2.4) — what's under test here is the repository's
/// routing.
/// </summary>
public class ShiftRepositoryDataLevelAccessTests
{
    private static ShiftRepository<VehicleDbContext, VehicleEntity, VehicleListDTO, VehicleListDTO> Repository(
        VehicleDbContext db, Action<ShiftRepositoryOptions<VehicleEntity>>? configure = null,
        IShiftEntityMapper<VehicleEntity, VehicleListDTO, VehicleListDTO>? mapper = null)
        => new(db, mapper ?? new ThrowingVehicleMapper(), configure);

    /// <summary>The canonical declaration — one Companies dimension, OR across the two legs.</summary>
    private static void DeclareCompanyOr(ShiftRepositoryOptions<VehicleEntity> options)
        => options.DataLevelAccess(access =>
            access.On(VehicleDataLevel.Companies).Keys(x => x.CompanyID, x => x.IntermediaryCompanyID));

    /// <summary>One canonical sample vehicle (see <see cref="VehicleScenario.SampleVehicles"/>) as a detached entity.</summary>
    private static VehicleEntity Sample(long id) => VehicleEntity.FromSamples().Single(v => v.ID == id);

    private static VehicleListDTO Dto(long? companyId, long? intermediaryCompanyId, string name = "updated")
        => new() { Name = name, CompanyID = companyId, IntermediaryCompanyID = intermediaryCompanyId };

    /// <summary>An <c>UpsertAsync</c> call with the row-path-relevant arguments surfaced.</summary>
    private static async Task<VehicleEntity> Upsert(
        ShiftRepository<VehicleDbContext, VehicleEntity, VehicleListDTO, VehicleListDTO> repository,
        VehicleEntity entity, VehicleListDTO dto, ActionTypes actionType, bool disableDefaultDataLevelAccess = false)
        => await repository.UpsertAsync(
            entity, dto, actionType, userId: null, idempotencyKey: null,
            disableDefaultDataLevelAccess: disableDefaultDataLevelAccess, disableGlobalFilters: false);

    private static async Task<ShiftEntityException> AssertDenied(string expectedMessage, Func<Task> operation)
    {
        var exception = await Assert.ThrowsAsync<ShiftEntityException>(operation);
        Assert.Equal((int)HttpStatusCode.Forbidden, exception.HttpStatusCode);
        Assert.Equal(expectedMessage, exception.Message.Body);
        return exception;
    }

    /// <summary>The IDs <c>GetIQueryable</c> exposes (no temporal, no includes, both filter kinds on).</summary>
    private static async Task<List<long>> VisibleIds(
        ShiftRepository<VehicleDbContext, VehicleEntity, VehicleListDTO, VehicleListDTO> repository,
        bool disableDefaultDataLevelAccess = false)
    {
        var query = await repository.GetIQueryable(
            asOf: null, includes: null,
            disableDefaultDataLevelAccess: disableDefaultDataLevelAccess, disableGlobalFilters: false);

        return query.Select(v => v.ID).OrderBy(id => id).ToList();
    }

    [Fact]
    public async Task PolicyDeclared_AppliesV2Filter_AndReplacesLegacy()
    {
        // The Intermediary (company 4) behind a real repository: the declared cross-column OR must surface the owner
        // leg #3 AND the intermediary legs #4/#5/#6 — the exact rows the legacy single-column filter could not.
        var legacy = new RecordingDefaultDataLevelAccess();
        using var provider = RepositoryHost.Build(legacy: legacy);
        using var scope = provider.CreateScope();

        var repository = Repository(RepositoryHost.SeededDb(scope), options =>
            options.DataLevelAccess(access =>
                access.On(VehicleDataLevel.Companies).Keys(x => x.CompanyID, x => x.IntermediaryCompanyID)));

        Assert.Equal(new long[] { 3, 4, 5, 6 }, await VisibleIds(repository));

        // The declaration is the whole truth: the legacy default filters did not also run.
        Assert.Equal(0, legacy.ApplyFilterCalls);
    }

    [Fact]
    public async Task PolicyDeclared_QueryPathFiltersAtReadLevel()
    {
        // Querying is a View ⇒ the Read level (D6). A Write-only grant must surface nothing on the query path —
        // pinning that the repository asks the policy for Read, not some wider level.
        using var provider = RepositoryHost.Build(
            typeAuth: () => ScopedTypeAuth.ToCompany(Companies.Intermediary, Access.Write));
        using var scope = provider.CreateScope();

        var repository = Repository(RepositoryHost.SeededDb(scope), options =>
            options.DataLevelAccess(access =>
                access.On(VehicleDataLevel.Companies).Keys(x => x.CompanyID, x => x.IntermediaryCompanyID)));

        Assert.Empty(await VisibleIds(repository));
    }

    [Fact]
    public async Task NoPolicy_RoutesThroughLegacyDefaultFilters()
    {
        // No declaration ⇒ today's legacy path, handed the entity's own DefaultDataLevelAccessOptions, and its
        // returned query is what the repository uses (the recording fake filters to nothing — an unmistakable marker).
        var legacy = new RecordingDefaultDataLevelAccess();
        using var provider = RepositoryHost.Build(legacy: legacy);
        using var scope = provider.CreateScope();

        var repository = Repository(RepositoryHost.SeededDb(scope));

        Assert.Empty(await VisibleIds(repository)); // the marker filter applied ⇒ the legacy-returned query is used
        Assert.Equal(1, legacy.ApplyFilterCalls);
        Assert.Same(repository.ShiftRepositoryOptions.DefaultDataLevelAccessOptions, legacy.LastOptions);
    }

    [Fact]
    public async Task Unscoped_AppliesNoFilterFromEitherPath_AndNeedsNoContext()
    {
        // An explicit Unscoped() opts the entity out of data-level filtering entirely: every row is visible and the
        // legacy filters don't run. The host deliberately omits AddShiftEntityDataLevelAccess() — an unscoped entity
        // needs no per-request context, so it must keep working on hosts that never registered one.
        var legacy = new RecordingDefaultDataLevelAccess();
        using var provider = RepositoryHost.Build(legacy: legacy, withDataLevelAccess: false);
        using var scope = provider.CreateScope();

        var repository = Repository(RepositoryHost.SeededDb(scope), options =>
            options.DataLevelAccess(access => access.Unscoped()));

        Assert.Equal(new long[] { 1, 2, 3, 4, 5, 6, 7 }, await VisibleIds(repository));
        Assert.Equal(0, legacy.ApplyFilterCalls);
    }

    [Fact]
    public async Task DisableFlag_BypassesBothPaths()
    {
        // disableDefaultDataLevelAccess: true is the caller's explicit bypass (reload-after-save and the
        // controller plumbing rely on it) — with it set, neither the declared policy nor the legacy filters run.
        var legacy = new RecordingDefaultDataLevelAccess();
        using var provider = RepositoryHost.Build(legacy: legacy);
        using var scope = provider.CreateScope();

        var repository = Repository(RepositoryHost.SeededDb(scope), options =>
            options.DataLevelAccess(access =>
                access.On(VehicleDataLevel.Companies).Keys(x => x.CompanyID, x => x.IntermediaryCompanyID)));

        Assert.Equal(
            new long[] { 1, 2, 3, 4, 5, 6, 7 },
            await VisibleIds(repository, disableDefaultDataLevelAccess: true));
        Assert.Equal(0, legacy.ApplyFilterCalls);
    }

    [Fact]
    public async Task PolicyDeclared_ContextNotRegistered_FailsClosed()
    {
        // A declared policy on a host that never registered the per-request context must throw — silently running
        // the query unfiltered would leak every out-of-scope row. The message points at the missing registration.
        using var provider = RepositoryHost.Build(withDataLevelAccess: false);
        using var scope = provider.CreateScope();

        var repository = Repository(RepositoryHost.SeededDb(scope), options =>
            options.DataLevelAccess(access =>
                access.On(VehicleDataLevel.Companies).Keys(x => x.CompanyID, x => x.IntermediaryCompanyID)));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () => await VisibleIds(repository));

        Assert.Contains("AddShiftEntityDataLevelAccess", exception.Message);
    }

    // ──────────────────────────────────────────────────────────────────────────────────────────────────────────────
    // Slice 3.2 — the row paths (FindAsync / UpsertAsync / DeleteAsync) route to policy.Authorize at the
    // operation's level (Find ⇒ Read, Insert/Edit ⇒ Write, Delete ⇒ Delete — D6), else today's legacy
    // HasDefaultDataLevelAccess. Same routing shape as the query path above.
    // ──────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PolicyDeclared_Find_AuthorizesTheIntermediaryLeg_AndReplacesLegacyRowCheck()
    {
        // The Intermediary (company 4) finds #4 — a row reachable only through the intermediary leg, the exact row
        // the legacy single-column row check wrongly denied (0.3's defect #2). The v2 row check (OR across both
        // legs, at Read) authorizes it, and the legacy row check is never consulted.
        var legacy = new RecordingDefaultDataLevelAccess();
        using var provider = RepositoryHost.Build(legacy: legacy);
        using var scope = provider.CreateScope();

        var repository = Repository(RepositoryHost.SeededDb(scope), DeclareCompanyOr);

        var found = await repository.FindAsync(4, asOf: null, disableDefaultDataLevelAccess: false, disableGlobalFilters: false);

        Assert.NotNull(found);
        Assert.Equal(4, found!.ID);
        Assert.Equal(0, legacy.RowCheckCalls);
    }

    [Fact]
    public async Task PolicyDeclared_Find_OutOfScopeId_ReturnsNullInsteadOfDenying()
    {
        // An out-of-scope id never reaches the row check: the query path (3.1) already filtered it, the Find comes
        // back empty, and a null entity has nothing to authorize — so the caller gets null (today's 404-shaped
        // outcome), not a 403. The 404-vs-403 choice proper is slice 3.3's denied-behavior option.
        var legacy = new RecordingDefaultDataLevelAccess();
        using var provider = RepositoryHost.Build(legacy: legacy);
        using var scope = provider.CreateScope();

        var repository = Repository(RepositoryHost.SeededDb(scope), DeclareCompanyOr);

        var found = await repository.FindAsync(1, asOf: null, disableDefaultDataLevelAccess: false, disableGlobalFilters: false);

        Assert.Null(found); // #1 is DealerA-owned with no intermediary leg — invisible to the Intermediary
        Assert.Equal(0, legacy.RowCheckCalls);
    }

    [Fact]
    public async Task NoPolicy_Find_RowChecksEvenTheNullEntity_AtReadLevel()
    {
        // Legacy parity: with no declaration, BaseFindAsync row-checks whatever it fetched — even a null entity
        // (today's behavior, kept byte-for-byte) — at the Read level, handing the entity's own options through.
        // The recording fake's marker filter guarantees the fetch misses.
        var legacy = new RecordingDefaultDataLevelAccess();
        using var provider = RepositoryHost.Build(legacy: legacy);
        using var scope = provider.CreateScope();

        var repository = Repository(RepositoryHost.SeededDb(scope));

        var found = await repository.FindAsync(3, asOf: null, disableDefaultDataLevelAccess: false, disableGlobalFilters: false);

        Assert.Null(found);
        Assert.Equal(1, legacy.RowCheckCalls);
        Assert.Null(legacy.LastRowCheckEntity);
        Assert.Equal(Access.Read, legacy.LastRowCheckAccess);
        Assert.Same(repository.ShiftRepositoryOptions.DefaultDataLevelAccessOptions, legacy.LastRowCheckOptions);
    }

    [Fact]
    public async Task NoPolicy_Find_LegacyDenial_Maps403CanNotReadItem()
    {
        // The legacy verdict still maps to the same denial as today: ShiftEntityException, 403, "Can Not Read Item".
        var legacy = new RecordingDefaultDataLevelAccess { RowCheckVerdict = false };
        using var provider = RepositoryHost.Build(legacy: legacy);
        using var scope = provider.CreateScope();

        var repository = Repository(RepositoryHost.SeededDb(scope));

        await AssertDenied("Can Not Read Item", () =>
            repository.FindAsync(3, asOf: null, disableDefaultDataLevelAccess: false, disableGlobalFilters: false));
    }

    [Fact]
    public async Task PolicyDeclared_Upsert_AuthorizesEitherLeg_AtWriteLevel()
    {
        // The row path runs the same cross-column OR as the query path: an Insert whose only in-scope leg is the
        // intermediary one passes for the Intermediary (full RWD grant), and the legacy row check never runs.
        var legacy = new RecordingDefaultDataLevelAccess();
        using var provider = RepositoryHost.Build(legacy: legacy);
        using var scope = provider.CreateScope();

        var repository = Repository(RepositoryHost.SeededDb(scope), DeclareCompanyOr, new UpsertVehicleMapper());

        var entity = await Upsert(repository, new VehicleEntity(), Dto(Companies.DealerA, Companies.Intermediary), ActionTypes.Insert);

        Assert.Equal(Companies.Intermediary, entity.IntermediaryCompanyID);
        Assert.Equal(0, legacy.RowCheckCalls);
    }

    [Fact]
    public async Task PolicyDeclared_Upsert_ReadOnlyGrant_GatesWrite()
    {
        // Level-per-operation (D6) — the row path's reason to exist: a Read-only grant lets the caller SEE company
        // 4's rows (query path, pinned above) but an Edit of one — legs unchanged, fully in scope — is denied,
        // because Upsert asks for Write. This is what closes 0.3's defect #1.
        using var provider = RepositoryHost.Build(
            typeAuth: () => ScopedTypeAuth.ToCompany(Companies.Intermediary, Access.Read));
        using var scope = provider.CreateScope();

        var repository = Repository(RepositoryHost.SeededDb(scope), DeclareCompanyOr, new UpsertVehicleMapper());
        var vehicle = Sample(3); // CompanyID == 4: visible to the caller, but not writable

        await AssertDenied("Can Not Update Item", () =>
            Upsert(repository, vehicle, Dto(vehicle.CompanyID, vehicle.IntermediaryCompanyID), ActionTypes.Update));
    }

    [Fact]
    public async Task PolicyDeclared_Upsert_ChecksTheMappedEntity()
    {
        // What gets authorized is the MAPPED entity — the values about to be saved, not the row as fetched: #4 is
        // in scope via its intermediary leg, but the DTO moves both legs out of scope, so the Edit is denied even
        // though the pre-mapping entity would have passed.
        using var provider = RepositoryHost.Build();
        using var scope = provider.CreateScope();

        var repository = Repository(RepositoryHost.SeededDb(scope), DeclareCompanyOr, new UpsertVehicleMapper());

        await AssertDenied("Can Not Update Item", () =>
            Upsert(repository, Sample(4), Dto(Companies.DealerA, null), ActionTypes.Update));
    }

    [Fact]
    public async Task PolicyDeclared_Upsert_InsertOutOfScope_Denied403CanNotCreateItem()
    {
        // Defect #1's worst case, now closed at the repository: an Insert wholly outside the caller's scope is
        // denied with the Insert-flavored message — previously unguarded whenever the legacy Company filter had to
        // be disabled to hand-write the OR query.
        using var provider = RepositoryHost.Build();
        using var scope = provider.CreateScope();

        var repository = Repository(RepositoryHost.SeededDb(scope), DeclareCompanyOr, new UpsertVehicleMapper());

        await AssertDenied("Can Not Create Item", () =>
            Upsert(repository, new VehicleEntity(), Dto(Companies.DealerA, null), ActionTypes.Insert));
    }

    [Fact]
    public async Task NoPolicy_Upsert_RoutesThroughLegacyRowCheck_AtWriteLevel_WithTheMappedEntity()
    {
        // Legacy parity: no declaration ⇒ today's HasDefaultDataLevelAccess at Write, handed the mapped entity
        // (the DTO's values applied) and the entity's own options; a false verdict maps to 403 "Can Not Create Item".
        var legacy = new RecordingDefaultDataLevelAccess { RowCheckVerdict = false };
        using var provider = RepositoryHost.Build(legacy: legacy);
        using var scope = provider.CreateScope();

        var repository = Repository(RepositoryHost.SeededDb(scope), configure: null, new UpsertVehicleMapper());
        var entity = new VehicleEntity();

        await AssertDenied("Can Not Create Item", () =>
            Upsert(repository, entity, Dto(Companies.DealerA, null), ActionTypes.Insert));

        Assert.Equal(1, legacy.RowCheckCalls);
        Assert.Same(entity, legacy.LastRowCheckEntity); // the very instance the mapper wrote onto
        Assert.Equal(Companies.DealerA, ((VehicleEntity)legacy.LastRowCheckEntity!).CompanyID); // post-mapping values
        Assert.Equal(Access.Write, legacy.LastRowCheckAccess);
        Assert.Same(repository.ShiftRepositoryOptions.DefaultDataLevelAccessOptions, legacy.LastRowCheckOptions);
    }

    [Fact]
    public async Task PolicyDeclared_Delete_ReadWriteGrant_GatesDelete_BeforeSoftDeleteFlag()
    {
        // The Delete level is its own gate (D6): Read+Write on company 4 does not authorize a Delete, and the
        // denial lands BEFORE the soft-delete flag is touched — the entity must come out unmodified.
        var legacy = new RecordingDefaultDataLevelAccess();
        using var provider = RepositoryHost.Build(
            typeAuth: () => ScopedTypeAuth.ToCompany(Companies.Intermediary, Access.Read, Access.Write),
            legacy: legacy);
        using var scope = provider.CreateScope();

        var repository = Repository(RepositoryHost.SeededDb(scope), DeclareCompanyOr);
        var vehicle = Sample(3);

        await AssertDenied("Can Not Delete Item", () =>
            repository.DeleteAsync(vehicle, isHardDelete: false, userId: null,
                disableDefaultDataLevelAccess: false, disableGlobalFilters: false).AsTask());

        Assert.False(vehicle.IsDeleted);
        Assert.Equal(0, legacy.RowCheckCalls);
    }

    [Fact]
    public async Task PolicyDeclared_Delete_DeleteGrant_SoftDeletes()
    {
        // With the Delete level granted the v2 check passes and the existing soft-delete behavior proceeds.
        using var provider = RepositoryHost.Build();
        using var scope = provider.CreateScope();

        var repository = Repository(RepositoryHost.SeededDb(scope), DeclareCompanyOr);
        var vehicle = Sample(3);

        var deleted = await repository.DeleteAsync(vehicle, isHardDelete: false, userId: null,
            disableDefaultDataLevelAccess: false, disableGlobalFilters: false);

        Assert.True(deleted.IsDeleted);
    }

    [Fact]
    public async Task NoPolicy_Delete_RoutesThroughLegacyRowCheck_AtDeleteLevel()
    {
        // Legacy parity: no declaration ⇒ today's HasDefaultDataLevelAccess at Delete; a false verdict maps to
        // 403 "Can Not Delete Item" before the soft-delete flag is touched.
        var legacy = new RecordingDefaultDataLevelAccess { RowCheckVerdict = false };
        using var provider = RepositoryHost.Build(legacy: legacy);
        using var scope = provider.CreateScope();

        var repository = Repository(RepositoryHost.SeededDb(scope));
        var vehicle = Sample(3);

        await AssertDenied("Can Not Delete Item", () =>
            repository.DeleteAsync(vehicle, isHardDelete: false, userId: null,
                disableDefaultDataLevelAccess: false, disableGlobalFilters: false).AsTask());

        Assert.False(vehicle.IsDeleted);
        Assert.Equal(Access.Delete, legacy.LastRowCheckAccess);
    }

    [Fact]
    public async Task Unscoped_RowPaths_SkipBothChecks_AndNeedNoContext()
    {
        // An explicit Unscoped() opts the row paths out exactly like the query path: Find/Insert/Delete all pass
        // with no check from either arm, on a host that never registered the per-request context (the short-circuit
        // must come before context resolution, or unscoped entities would break on such hosts).
        var legacy = new RecordingDefaultDataLevelAccess();
        using var provider = RepositoryHost.Build(legacy: legacy, withDataLevelAccess: false);
        using var scope = provider.CreateScope();

        var repository = Repository(RepositoryHost.SeededDb(scope),
            options => options.DataLevelAccess(access => access.Unscoped()),
            new UpsertVehicleMapper());

        var found = await repository.FindAsync(1, asOf: null, disableDefaultDataLevelAccess: false, disableGlobalFilters: false);
        var inserted = await Upsert(repository, new VehicleEntity(), Dto(Companies.DealerA, null), ActionTypes.Insert);
        var deleted = await repository.DeleteAsync(Sample(2), isHardDelete: false, userId: null,
            disableDefaultDataLevelAccess: false, disableGlobalFilters: false);

        Assert.NotNull(found);
        Assert.Equal(Companies.DealerA, inserted.CompanyID);
        Assert.True(deleted.IsDeleted);
        Assert.Equal(0, legacy.RowCheckCalls);
        Assert.Equal(0, legacy.ApplyFilterCalls);
    }

    [Fact]
    public async Task DisableFlag_BypassesBothRowChecks()
    {
        // disableDefaultDataLevelAccess: true is the caller's explicit bypass on the row paths too (the controller
        // plumbing relies on it) — with it set, neither the declared policy nor the legacy row check runs, even for
        // wholly out-of-scope writes.
        var legacy = new RecordingDefaultDataLevelAccess();
        using var provider = RepositoryHost.Build(legacy: legacy);
        using var scope = provider.CreateScope();

        var repository = Repository(RepositoryHost.SeededDb(scope), DeclareCompanyOr, new UpsertVehicleMapper());

        var inserted = await Upsert(repository, new VehicleEntity(), Dto(Companies.DealerA, null), ActionTypes.Insert,
            disableDefaultDataLevelAccess: true);
        var deleted = await repository.DeleteAsync(Sample(1), isHardDelete: false, userId: null,
            disableDefaultDataLevelAccess: true, disableGlobalFilters: false);

        Assert.Equal(Companies.DealerA, inserted.CompanyID);
        Assert.True(deleted.IsDeleted);
        Assert.Equal(0, legacy.RowCheckCalls);
    }

    [Fact]
    public async Task PolicyDeclared_ContextNotRegistered_RowPathFailsClosed()
    {
        // The row paths fail closed exactly like the query path: a declared policy with no resolvable per-request
        // context throws (naming the missing registration) rather than authorizing unguarded. Upsert exercises this
        // directly — it is reachable without the query path ever running.
        using var provider = RepositoryHost.Build(withDataLevelAccess: false);
        using var scope = provider.CreateScope();

        var repository = Repository(RepositoryHost.SeededDb(scope), DeclareCompanyOr, new UpsertVehicleMapper());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Upsert(repository, new VehicleEntity(), Dto(Companies.Intermediary, null), ActionTypes.Insert));

        Assert.Contains("AddShiftEntityDataLevelAccess", exception.Message);
    }
}
