using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.ShiftEntity.Model.Flags;
using ShiftSoftware.TypeAuth.Core;

namespace ShiftSoftware.ShiftEntity.Tests.DataLevelAccess.Scenario;

/// <summary>EF context for the repository-level tests (Phase 3) — just the scenario vehicles.</summary>
public class VehicleDbContext : ShiftDbContext
{
    public DbSet<VehicleEntity> Vehicles { get; set; } = default!;

    public VehicleDbContext(DbContextOptions<VehicleDbContext> options) : base(options) { }
}

/// <summary>Minimal list/view DTO — the repository's type parameters demand one; these tests never map.</summary>
public class VehicleListDTO : ShiftEntityDTOBase
{
    public override string? ID { get; set; }
}

/// <summary>
/// <see cref="IShiftEntityMapper{TEntity,TListDTO,TViewDTO}"/> double for paths that must not map (the
/// <c>GetIQueryable</c> tests) — every member throws, failing loud if a path unexpectedly maps.
/// </summary>
public sealed class ThrowingVehicleMapper : IShiftEntityMapper<VehicleEntity, VehicleListDTO, VehicleListDTO>
{
    public VehicleListDTO MapToView(VehicleEntity entity) => throw new NotSupportedException();
    public VehicleEntity MapToEntity(VehicleListDTO dto, VehicleEntity existing) => throw new NotSupportedException();
    public IQueryable<VehicleListDTO> MapToList(IQueryable<VehicleEntity> query) => throw new NotSupportedException();
    public void CopyEntity(VehicleEntity source, VehicleEntity target) => throw new NotSupportedException();
}

/// <summary>
/// Recording <see cref="IDefaultDataLevelAccess"/> double — the legacy arm of the Phase 3 routing tests.
/// <see cref="ApplyDefaultDataLevelFilters{EntityType}"/> records the call (and the options it was handed) and
/// applies an unmistakable marker filter (matches nothing), so a test can assert both that the repository routed
/// through the legacy path <em>and</em> that it used the returned query. Members the repository's query path does
/// not touch throw, to fail loud if a path unexpectedly depends on them.
/// </summary>
public sealed class RecordingDefaultDataLevelAccess : IDefaultDataLevelAccess
{
    public int ApplyFilterCalls { get; private set; }
    public DefaultDataLevelAccessOptions? LastOptions { get; private set; }

    public IQueryable<EntityType> ApplyDefaultDataLevelFilters<EntityType>(
        DefaultDataLevelAccessOptions DefaultDataLevelAccessOptions, IQueryable<EntityType> query) where EntityType : notnull
    {
        ApplyFilterCalls++;
        LastOptions = DefaultDataLevelAccessOptions;
        return query.Where(_ => false); // marker: a legacy-filtered query returns no rows in these tests
    }

    public bool HasDefaultDataLevelAccess<EntityType>(
        DefaultDataLevelAccessOptions defaultDataLevelAccessOptions, EntityType? entity, Access access)
        where EntityType : ShiftEntity<EntityType>, new()
        => throw new NotSupportedException(); // the row paths are slice 3.2

    public List<long?>? GetAccessibleCountries() => throw new NotSupportedException();
    public List<long?>? GetAccessibleRegions() => throw new NotSupportedException();
    public List<long?>? GetAccessibleCompanies() => throw new NotSupportedException();
    public List<long?>? GetAccessibleBranches() => throw new NotSupportedException();
    public List<long?>? GetAccessibleBrands() => throw new NotSupportedException();
    public List<long?>? GetAccessibleCities() => throw new NotSupportedException();
    public List<long?>? GetAccessibleTeams() => throw new NotSupportedException();

    public IQueryable<EntityType> ApplyDefaultCountryFilter<EntityType>(IQueryable<EntityType> query) where EntityType : IEntityHasCountry<EntityType> => throw new NotSupportedException();
    public IQueryable<EntityType> ApplyDefaultRegionFilter<EntityType>(IQueryable<EntityType> query) where EntityType : IEntityHasRegion<EntityType> => throw new NotSupportedException();
    public IQueryable<EntityType> ApplyDefaultCompanyFilter<EntityType>(IQueryable<EntityType> query) where EntityType : IEntityHasCompany<EntityType> => throw new NotSupportedException();
    public IQueryable<EntityType> ApplyDefaultBranchFilter<EntityType>(IQueryable<EntityType> query) where EntityType : IEntityHasCompanyBranch<EntityType> => throw new NotSupportedException();
    public IQueryable<EntityType> ApplyDefaultBrandFilter<EntityType>(IQueryable<EntityType> query) where EntityType : IEntityHasBrand<EntityType> => throw new NotSupportedException();
    public IQueryable<EntityType> ApplyDefaultCityFilter<EntityType>(IQueryable<EntityType> query) where EntityType : IEntityHasCity<EntityType> => throw new NotSupportedException();
    public IQueryable<EntityType> ApplyDefaultTeamFilter<EntityType>(IQueryable<EntityType> query) where EntityType : IEntityHasTeam<EntityType> => throw new NotSupportedException();
}

/// <summary>
/// DI host shaped like a real web app for repository-level tests: EF InMemory <see cref="VehicleDbContext"/>
/// (fresh database per host), scoped <see cref="ITypeAuthService"/> (the scenario's scoped
/// <see cref="TypeAuthContext"/>), scoped <see cref="ICurrentUserProvider"/> + <see cref="IdentityClaimProvider"/>,
/// singleton <see cref="IHashIdService"/>, the recording legacy <see cref="IDefaultDataLevelAccess"/>, and — unless
/// a test opts out to prove fail-closed behavior — <c>AddShiftEntityDataLevelAccess()</c>. Scope validation is on,
/// the same startup check a Development host runs.
/// </summary>
public static class RepositoryHost
{
    public static ServiceProvider Build(
        Func<ITypeAuthService>? typeAuth = null,
        RecordingDefaultDataLevelAccess? legacy = null,
        bool withDataLevelAccess = true,
        Func<ICurrentUserProvider>? currentUser = null)
    {
        var services = new ServiceCollection();

        services.AddDbContext<VehicleDbContext>(options =>
            options.UseInMemoryDatabase(Guid.NewGuid().ToString()));

        // What TypeAuth's AddTypeAuth contributes: a per-request ITypeAuthService built from the caller's claims.
        services.AddScoped<ITypeAuthService>(_ => (typeAuth ?? (() => ScopedTypeAuth.ToCompany(Companies.Intermediary)))());
        // What AddShiftEntityWebSharedCore / AddShiftEntity contribute.
        services.AddScoped<ICurrentUserProvider>(_ => (currentUser ?? FakeCurrentUserProvider.Anonymous)());
        services.AddScoped<IdentityClaimProvider>();
        services.AddSingleton<IHashIdService>(new RecordingHashIdService());
        services.AddSingleton<IDefaultDataLevelAccess>(legacy ?? new RecordingDefaultDataLevelAccess());

        if (withDataLevelAccess)
            services.AddShiftEntityDataLevelAccess();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    /// <summary>The scope's <see cref="VehicleDbContext"/>, seeded with the canonical sample vehicles.</summary>
    public static VehicleDbContext SeededDb(IServiceScope scope)
    {
        var db = scope.ServiceProvider.GetRequiredService<VehicleDbContext>();
        db.Vehicles.AddRange(VehicleEntity.FromSamples());
        db.SaveChanges();
        return db;
    }
}
