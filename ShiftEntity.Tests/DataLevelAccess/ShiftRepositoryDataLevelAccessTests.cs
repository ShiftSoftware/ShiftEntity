using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.Core.DataLevelAccess;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.Tests.DataLevelAccess.Scenario;
using ShiftSoftware.TypeAuth.Core;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.DataLevelAccess;

/// <summary>
/// Slice 3.1 — <c>ShiftRepository.GetIQueryable</c> routes the query-path data-level filter: a declared v2 policy
/// replaces the legacy default filters for the entity entirely (opt-in coexistence, D1 — no declaration ⇒ today's
/// legacy path, byte-for-byte the same call), an explicit <c>Unscoped()</c> applies no filter from either path, and
/// a declared policy whose per-request <see cref="DataLevelAccessContext"/> cannot be resolved fails closed rather
/// than running unfiltered. Exercised through a <em>real</em> repository over the EF InMemory provider, in a DI host
/// shaped like production (see <see cref="RepositoryHost"/>); the engine semantics themselves are pinned at the
/// policy level (2.3) — what's under test here is the repository's routing.
/// </summary>
public class ShiftRepositoryDataLevelAccessTests
{
    private static ShiftRepository<VehicleDbContext, VehicleEntity, VehicleListDTO, VehicleListDTO> Repository(
        VehicleDbContext db, Action<ShiftRepositoryOptions<VehicleEntity>>? configure = null)
        => new(db, new ThrowingVehicleMapper(), configure);

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
}
