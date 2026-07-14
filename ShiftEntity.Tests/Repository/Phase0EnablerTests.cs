using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.ShiftEntity.Tests.DataLevelAccess.Scenario;
using ShiftSoftware.TypeAuth.Core;
using System.Net;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Repository;

/// <summary>
/// Phase 0 of the ShiftIdentity modernization needs three framework enablers so an otherwise-simple entity can drop
/// its hand-written repository yet keep the cross-cutting protections that repository provided. These tests pin the
/// framework halves of all three:
/// <list type="number">
/// <item><b>Protected-row guard</b> — <c>ShiftRepository.UpsertAsync</c>/<c>DeleteAsync</c> reject an edit/delete of a
/// row whose <see cref="IShiftEntityProtectable.IsProtected"/> is true (403), replacing the per-repository guard.</item>
/// <item><b>Save validators</b> — every DI-registered <see cref="IShiftEntitySaveValidator"/> runs on the repository
/// save path with the pending unit of work, and can abort it (the seam ShiftIdentity's feature-locking uses).</item>
/// <item><b>Default data-level baseline</b> — the built-in repository picks up a host-registered
/// <see cref="DefaultDataLevelAccessOptions"/> instead of a fresh all-filters-enabled <c>new()</c>.</item>
/// </list>
/// The identity-side halves (the 6 entities implementing the marker, the feature-lock validator) live in ShiftIdentity.
/// </summary>
public class Phase0EnablerTests
{
    // A protectable entity (its bool IsProtected property satisfies the marker) — the shape of Country/Region/etc.
    private sealed class GuardedEntity : ShiftEntity<GuardedEntity>, IShiftEntityProtectable
    {
        public string Name { get; set; } = "";
        public bool IsProtected { get; set; }
    }

    private sealed class GuardedListDTO : ShiftEntityDTOBase
    {
        public override string? ID { get; set; }
        public string Name { get; set; } = "";
    }

    private sealed class GuardedDbContext : ShiftDbContext
    {
        public DbSet<GuardedEntity> Items { get; set; } = default!;
        public GuardedDbContext(DbContextOptions<GuardedDbContext> options) : base(options) { }
    }

    private sealed class GuardedMapper : IShiftEntityMapper<GuardedEntity, GuardedListDTO, GuardedListDTO>
    {
        public GuardedEntity MapToEntity(GuardedListDTO dto, GuardedEntity existing, MappingContext context = default)
        {
            existing.Name = dto.Name;
            return existing;
        }
        public GuardedListDTO MapToView(GuardedEntity entity, MappingContext context = default)
            => new() { ID = entity.ID.ToString(), Name = entity.Name };
        public IQueryable<GuardedListDTO> MapToList(IQueryable<GuardedEntity> query, MappingContext context = default)
            => query.Select(e => new GuardedListDTO { ID = e.ID.ToString(), Name = e.Name });
        public void CopyEntity(GuardedEntity source, GuardedEntity target, MappingContext context = default)
            => target.Name = source.Name;
    }

    private sealed class RecordingSaveValidator : IShiftEntitySaveValidator
    {
        // Snapshot state/entities AT validation time — the live EntityEntry.State flips to Unchanged once the save
        // commits, so asserting on the entry after the fact would be misleading.
        public int Calls { get; private set; }
        public List<EntityState> States { get; } = new();
        public List<object> Entities { get; } = new();
        public void Validate(IReadOnlyList<EntityEntry> pendingWrites)
        {
            Calls++;
            States.Clear();
            Entities.Clear();
            foreach (var entry in pendingWrites)
            {
                States.Add(entry.State);
                Entities.Add(entry.Entity);
            }
        }
    }

    // Stands in for ShiftIdentity's feature-lock validator: refuses to save when a locked entity type is present.
    private sealed class BlockingSaveValidator : IShiftEntitySaveValidator
    {
        public void Validate(IReadOnlyList<EntityEntry> pendingWrites)
        {
            if (pendingWrites.Any(e => e.Entity is GuardedEntity))
                throw new ShiftEntityException(new Message("Locked", "Feature is locked"));
        }
    }

    /// <summary>A web-shaped DI host for the built-in repository over <see cref="GuardedDbContext"/>, mirroring the
    /// scenario <c>RepositoryHost</c> but parameterized with an optional registered default-data-level options and
    /// optional save validators.</summary>
    private static ServiceProvider BuildHost(
        DefaultDataLevelAccessOptions? dataLevelOptions = null,
        params IShiftEntitySaveValidator[] validators)
    {
        var services = new ServiceCollection();

        services.AddDbContext<GuardedDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddScoped<ITypeAuthService>(_ => ScopedTypeAuth.ToCompany(Companies.Intermediary));
        services.AddScoped<ICurrentUserProvider>(_ => FakeCurrentUserProvider.Anonymous());
        services.AddScoped<IdentityClaimProvider>();
        services.AddSingleton<IHashIdService>(new RecordingHashIdService());
        services.AddSingleton<IDefaultDataLevelAccess>(new RecordingDefaultDataLevelAccess());

        if (dataLevelOptions is not null)
            services.AddSingleton(dataLevelOptions);

        foreach (var validator in validators)
            services.AddSingleton(validator);

        services.AddShiftEntityDataLevelAccess();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static ShiftRepository<GuardedDbContext, GuardedEntity, GuardedListDTO, GuardedListDTO> Repo(IServiceScope scope)
        => new(scope.ServiceProvider.GetRequiredService<GuardedDbContext>(), o => o.UseMapper(new GuardedMapper()));

    // ---- 0.2 Protected-row guard --------------------------------------------------------------------------------

    [Fact]
    public async Task Upsert_UpdatingProtectedRow_IsRejectedWith403_BeforeMapping()
    {
        using var provider = BuildHost();
        using var scope = provider.CreateScope();
        var repo = Repo(scope);

        var existing = new GuardedEntity { Name = "seed", IsProtected = true };

        var ex = await Assert.ThrowsAsync<ShiftEntityException>(async () =>
            await repo.UpsertAsync(existing, new GuardedListDTO { Name = "changed" }, ActionTypes.Update,
                userId: null, idempotencyKey: null, disableDefaultDataLevelAccess: true, disableGlobalFilters: true));

        Assert.Equal((int)HttpStatusCode.Forbidden, ex.HttpStatusCode);
        Assert.Equal("seed", existing.Name); // the guard fired before MapToEntity ran
    }

    [Fact]
    public async Task Delete_ProtectedRow_IsRejectedWith403_BeforeSoftDelete()
    {
        using var provider = BuildHost();
        using var scope = provider.CreateScope();
        var repo = Repo(scope);

        var existing = new GuardedEntity { Name = "seed", IsProtected = true };

        var ex = await Assert.ThrowsAsync<ShiftEntityException>(async () =>
            await repo.DeleteAsync(existing, userId: null, disableDefaultDataLevelAccess: true, disableGlobalFilters: true));

        Assert.Equal((int)HttpStatusCode.Forbidden, ex.HttpStatusCode);
        Assert.False(existing.IsDeleted); // never soft-deleted
    }

    [Fact]
    public async Task Upsert_NonProtectedRow_PassesTheGuardAndMaps()
    {
        using var provider = BuildHost();
        using var scope = provider.CreateScope();
        var repo = Repo(scope);

        var existing = new GuardedEntity { Name = "old", IsProtected = false };

        var result = await repo.UpsertAsync(existing, new GuardedListDTO { Name = "new" }, ActionTypes.Update,
            userId: null, idempotencyKey: null, disableDefaultDataLevelAccess: true, disableGlobalFilters: true);

        Assert.Equal("new", result.Name); // guard passed; mapping ran
    }

    [Fact]
    public async Task Insert_NewRow_IsNotBlocked_EvenThoughGuardIsPresent()
    {
        // A fresh entity is IsProtected == false, so the guard never blocks an Insert — you cannot create protected
        // data through the CRUD path, exactly as the per-repository guard behaved.
        using var provider = BuildHost();
        using var scope = provider.CreateScope();
        var repo = Repo(scope);

        var result = await repo.UpsertAsync(new GuardedEntity(), new GuardedListDTO { Name = "created" }, ActionTypes.Insert,
            userId: null, idempotencyKey: null, disableDefaultDataLevelAccess: true, disableGlobalFilters: true);

        Assert.Equal("created", result.Name);
    }

    // ---- 0.1 Save validators ------------------------------------------------------------------------------------

    [Fact]
    public async Task SaveChanges_InvokesRegisteredValidators_WithThePendingUnitOfWork()
    {
        var recorder = new RecordingSaveValidator();
        using var provider = BuildHost(validators: recorder);
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GuardedDbContext>();
        var repo = Repo(scope);

        db.Items.Add(new GuardedEntity { Name = "row" });
        var saved = await repo.SaveChangesAsync();

        Assert.Equal(1, recorder.Calls);
        Assert.Equal(EntityState.Added, Assert.Single(recorder.States)); // seen as Added at validation time
        Assert.IsType<GuardedEntity>(Assert.Single(recorder.Entities));
        Assert.Equal(1, saved); // save proceeded after the validator allowed it
    }

    [Fact]
    public async Task SaveChanges_WhenAValidatorThrows_AbortsTheSave_NothingPersisted()
    {
        using var provider = BuildHost(validators: new BlockingSaveValidator());
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GuardedDbContext>();
        var repo = Repo(scope);

        db.Items.Add(new GuardedEntity { Name = "row" });

        await Assert.ThrowsAsync<ShiftEntityException>(() => repo.SaveChangesAsync());

        using var verifyScope = provider.CreateScope();
        var freshDb = verifyScope.ServiceProvider.GetRequiredService<GuardedDbContext>();
        Assert.Empty(freshDb.Items); // the validator threw before db.SaveChanges, so nothing hit the store
    }

    [Fact]
    public async Task SaveChanges_WithNoValidatorsRegistered_ProceedsNormally()
    {
        using var provider = BuildHost();
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GuardedDbContext>();
        var repo = Repo(scope);

        db.Items.Add(new GuardedEntity { Name = "row" });
        var saved = await repo.SaveChangesAsync();

        Assert.Equal(1, saved);
    }

    // ---- 0.3 Default data-level baseline ------------------------------------------------------------------------

    [Fact]
    public void BuiltInRepo_AppliesTheHostRegisteredDefaultDataLevelAccessOptions()
    {
        var registered = new DefaultDataLevelAccessOptions { DisableDefaultCountryFilter = true };
        using var provider = BuildHost(dataLevelOptions: registered);
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GuardedDbContext>();

        // The built-in / auto-CRUD path: a bare ShiftRepository with no builder and no IConfiguresShiftRepository.
        var repo = new ShiftRepository<GuardedDbContext, GuardedEntity, GuardedListDTO, GuardedListDTO>(db);

        Assert.Same(registered, repo.ShiftRepositoryOptions.DefaultDataLevelAccessOptions);
        Assert.True(repo.ShiftRepositoryOptions.DefaultDataLevelAccessOptions.DisableDefaultCountryFilter);
    }

    [Fact]
    public void BuiltInRepo_WithoutRegistration_KeepsFreshDefaults()
    {
        using var provider = BuildHost(); // nothing registered under DefaultDataLevelAccessOptions
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GuardedDbContext>();

        var repo = new ShiftRepository<GuardedDbContext, GuardedEntity, GuardedListDTO, GuardedListDTO>(db);

        // A fresh new() (all default filters enabled) — consumers that register no default are unaffected.
        Assert.False(repo.ShiftRepositoryOptions.DefaultDataLevelAccessOptions.DisableDefaultCountryFilter);
    }
}
