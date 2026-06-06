using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.ShiftEntity.Tests.DataLevelAccess.Scenario;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Repository;

/// <summary>
/// Slice 3.4 — the named <see cref="RepositoryBypass"/> vocabulary replaces the positional
/// <c>disableDefaultDataLevelAccess</c>/<c>disableGlobalFilters</c> bool pair at call sites. The convenience
/// overloads are <b>additive sugar only</b>: non-virtual on <c>ShiftRepository</c> and default interface methods on
/// the operation interfaces, both forwarding into the original bool-taking members. Two invariants are pinned here:
/// <list type="number">
/// <item><b>Dispatch safety</b> — org repositories override the bool-taking virtuals (ShiftIdentity, Toyota-Iraq,
/// Toyota-Centralasia, ADP; ~25 sites). A call through the sugar must still dispatch through those overrides; the
/// sugar being non-virtual and forwarding inward guarantees it, and the tests prove it.</item>
/// <item><b>Flag mapping</b> — <see cref="RepositoryBypass.DataLevelAccess"/> maps to
/// <c>disableDefaultDataLevelAccess</c> and <see cref="RepositoryBypass.GlobalFilters"/> to
/// <c>disableGlobalFilters</c>, never swapped (a swap would silently bypass the wrong mechanism).</item>
/// </list>
/// The bool members stay un-deprecated this slice: obsoleting them punishes overriders who have no alternative seam
/// yet — that redesign (or a repository fork) is deferred to 4.8 with the vNext backlog (see
/// <c>.shift/repos/shift-entity/repository-vnext.md</c>).
/// </summary>
public class RepositoryBypassTests
{
    /// <summary>
    /// The consumer-override shape found across the orgs: override the bool-taking virtuals, do custom work, call
    /// base. Records what reached it so the tests can assert both that it ran and which bools it received.
    /// </summary>
    private sealed class OverridingVehicleRepository
        : ShiftRepository<VehicleDbContext, VehicleEntity, VehicleListDTO, VehicleListDTO>
    {
        public int UpsertOverrideCalls { get; private set; }
        public (bool DataLevel, bool GlobalFilters)? UpsertBools { get; private set; }
        public int DeleteOverrideCalls { get; private set; }
        public (bool DataLevel, bool GlobalFilters)? DeleteBools { get; private set; }

        public OverridingVehicleRepository(VehicleDbContext db)
            : base(db, new UpsertVehicleMapper()) { }

        public override ValueTask<VehicleEntity> UpsertAsync(
            VehicleEntity entity, VehicleListDTO dto, ActionTypes actionType, long? userId, Guid? idempotencyKey,
            bool disableDefaultDataLevelAccess, bool disableGlobalFilters)
        {
            UpsertOverrideCalls++;
            UpsertBools = (disableDefaultDataLevelAccess, disableGlobalFilters);
            return base.UpsertAsync(entity, dto, actionType, userId, idempotencyKey,
                disableDefaultDataLevelAccess, disableGlobalFilters);
        }

        public override ValueTask<VehicleEntity> DeleteAsync(
            VehicleEntity entity, bool isHardDelete, long? userId,
            bool disableDefaultDataLevelAccess, bool disableGlobalFilters)
        {
            DeleteOverrideCalls++;
            DeleteBools = (disableDefaultDataLevelAccess, disableGlobalFilters);
            return base.DeleteAsync(entity, isHardDelete, userId,
                disableDefaultDataLevelAccess, disableGlobalFilters);
        }
    }

    /// <summary>
    /// A direct implementor of <see cref="IShiftEntityFind{EntityType}"/> that declares ONLY the bool members — the
    /// FakeUserRepository shape. That it compiles at all proves the sugar is a default interface method (an abstract
    /// addition would have broken every such implementor); the recorded arguments pin the exact flag→bool mapping.
    /// </summary>
    private sealed class RecordingFindImplementor : IShiftEntityFind<VehicleEntity>
    {
        public (long Id, DateTimeOffset? AsOf, bool DataLevel, bool GlobalFilters)? FindCall { get; private set; }
        public (Guid Key, DateTimeOffset? AsOf, bool DataLevel, bool GlobalFilters)? IdempotencyCall { get; private set; }

        public Task<VehicleEntity?> FindAsync(long id, DateTimeOffset? asOf, bool disableDefaultDataLevelAccess, bool disableGlobalFilters)
        {
            FindCall = (id, asOf, disableDefaultDataLevelAccess, disableGlobalFilters);
            return Task.FromResult<VehicleEntity?>(null);
        }

        public Task<VehicleEntity?> FindByIdempotencyKeyAsync(Guid idempotencyKey, DateTimeOffset? asOf, bool disableDefaultDataLevelAccess, bool disableGlobalFilters)
        {
            IdempotencyCall = (idempotencyKey, asOf, disableDefaultDataLevelAccess, disableGlobalFilters);
            return Task.FromResult<VehicleEntity?>(null);
        }

        public IQueryable<RevisionDTO> GetRevisionsAsync(long id) => throw new NotSupportedException();
        public Task<Stream> PrintAsync(string id) => throw new NotSupportedException();
    }

    private static VehicleListDTO Dto(long? companyId, long? intermediaryCompanyId)
        => new() { Name = "via sugar", CompanyID = companyId, IntermediaryCompanyID = intermediaryCompanyId };

    [Fact]
    public async Task Sugar_GetIQueryable_DefaultIsNoBypass()
    {
        // GetIQueryable() with no arguments must behave exactly like (asOf: null, includes: null, false, false):
        // the data-level mechanism runs (the recording legacy fake is invoked and its marker filter is honored).
        var legacy = new RecordingDefaultDataLevelAccess();
        using var provider = RepositoryHost.Build(legacy: legacy);
        using var scope = provider.CreateScope();

        var repository = new ShiftRepository<VehicleDbContext, VehicleEntity, VehicleListDTO, VehicleListDTO>(
            RepositoryHost.SeededDb(scope), new ThrowingVehicleMapper());

        var rows = (await repository.GetIQueryable()).ToList();

        Assert.Empty(rows); // the legacy marker filter (matches nothing) was applied ⇒ the mechanism ran
        Assert.Equal(1, legacy.ApplyFilterCalls);
    }

    [Fact]
    public async Task Sugar_GetIQueryable_BypassDataLevelAccess_SkipsOnlyThatMechanism()
    {
        // bypass: DataLevelAccess must map to disableDefaultDataLevelAccess (and NOT to disableGlobalFilters):
        // the legacy data-level filter is skipped and every seeded row comes back.
        var legacy = new RecordingDefaultDataLevelAccess();
        using var provider = RepositoryHost.Build(legacy: legacy);
        using var scope = provider.CreateScope();

        var repository = new ShiftRepository<VehicleDbContext, VehicleEntity, VehicleListDTO, VehicleListDTO>(
            RepositoryHost.SeededDb(scope), new ThrowingVehicleMapper());

        var rows = (await repository.GetIQueryable(bypass: RepositoryBypass.DataLevelAccess)).ToList();

        Assert.Equal(7, rows.Count);
        Assert.Equal(0, legacy.ApplyFilterCalls);

        // The complement: bypassing only the global filters must leave the data-level mechanism running.
        var filtered = (await repository.GetIQueryable(bypass: RepositoryBypass.GlobalFilters)).ToList();

        Assert.Empty(filtered);
        Assert.Equal(1, legacy.ApplyFilterCalls);
    }

    [Fact]
    public async Task Sugar_Find_DefaultsApplyThePolicy_AndBypassAllRevealsTheRow()
    {
        // Through a declared v2 policy: FindAsync(id) keeps the data-level filter (out-of-scope ⇒ null), and
        // FindAsync(id, bypass: All) is the named spelling of the caller bypass the plumbing uses (row revealed).
        using var provider = RepositoryHost.Build();
        using var scope = provider.CreateScope();

        var repository = new ShiftRepository<VehicleDbContext, VehicleEntity, VehicleListDTO, VehicleListDTO>(
            RepositoryHost.SeededDb(scope), new ThrowingVehicleMapper(), options =>
                options.DataLevelAccess(access =>
                    access.On(VehicleDataLevel.Companies).Keys(x => x.CompanyID, x => x.IntermediaryCompanyID)));

        Assert.Null(await repository.FindAsync(1));                                // #1 is out of the Intermediary's scope
        Assert.NotNull(await repository.FindAsync(1, bypass: RepositoryBypass.All)); // the explicit, named bypass
    }

    [Fact]
    public async Task Sugar_Upsert_DispatchesThroughTheOverriddenBoolVirtual()
    {
        // THE dispatch-safety pin: a repository that overrides the BOOL UpsertAsync (the org-wide consumer shape)
        // must receive calls made through the sugar — with the default mapping to (false, false).
        using var provider = RepositoryHost.Build();
        using var scope = provider.CreateScope();

        var repository = new OverridingVehicleRepository(RepositoryHost.SeededDb(scope));

        var entity = await repository.UpsertAsync(
            new VehicleEntity(), Dto(Companies.Intermediary, null), ActionTypes.Insert, userId: null);

        Assert.Equal(1, repository.UpsertOverrideCalls);
        Assert.Equal((false, false), repository.UpsertBools);
        Assert.Equal(Companies.Intermediary, entity.CompanyID); // base ran after the override: the mapped entity flowed back

        // The same holds when called through the interface (the CrudHandler's dispatch path), with flags mapped.
        IShiftRepositoryAsync<VehicleEntity, VehicleListDTO, VehicleListDTO> iface = repository;
        await iface.UpsertAsync(new VehicleEntity(), Dto(Companies.Intermediary, null), ActionTypes.Insert,
            userId: null, bypass: RepositoryBypass.All);

        Assert.Equal(2, repository.UpsertOverrideCalls);
        Assert.Equal((true, true), repository.UpsertBools);
    }

    [Fact]
    public async Task Sugar_Delete_DispatchesThroughTheOverriddenBoolVirtual()
    {
        // Same pin for DeleteAsync — the other commonly-overridden operation.
        using var provider = RepositoryHost.Build();
        using var scope = provider.CreateScope();

        var repository = new OverridingVehicleRepository(RepositoryHost.SeededDb(scope));
        var vehicle = VehicleEntity.FromSamples().Single(v => v.ID == 3);

        var deleted = await repository.DeleteAsync(vehicle, isHardDelete: false, userId: null);

        Assert.Equal(1, repository.DeleteOverrideCalls);
        Assert.Equal((false, false), repository.DeleteBools);
        Assert.True(deleted.IsDeleted);

        await repository.DeleteAsync(VehicleEntity.FromSamples().Single(v => v.ID == 4),
            isHardDelete: false, userId: null, bypass: RepositoryBypass.DataLevelAccess);

        Assert.Equal((true, false), repository.DeleteBools);
    }

    [Fact]
    public async Task DirectInterfaceImplementor_GetsTheSugarViaDefaultImplementation()
    {
        // A FakeUserRepository-shaped implementor (bool members only — no sugar declared) compiles unchanged and
        // inherits the sugar from the interface. The recorded bools pin the exact flag→bool mapping per position.
        var implementor = new RecordingFindImplementor();
        IShiftEntityFind<VehicleEntity> find = implementor;

        await find.FindAsync(42);
        Assert.Equal((42L, null, false, false), implementor.FindCall);

        await find.FindAsync(42, bypass: RepositoryBypass.GlobalFilters);
        Assert.Equal((42L, null, false, true), implementor.FindCall);

        var key = Guid.NewGuid();
        await find.FindByIdempotencyKeyAsync(key, bypass: RepositoryBypass.DataLevelAccess);
        Assert.Equal((key, null, true, false), implementor.IdempotencyCall);
    }
}
