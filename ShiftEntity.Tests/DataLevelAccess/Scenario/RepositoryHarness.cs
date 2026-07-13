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

/// <summary>
/// Minimal list/view-and-upsert DTO — the repository's type parameters demand one. The query-path tests never map;
/// the row-path (upsert) tests carry the two company legs through it, applied by <see cref="UpsertVehicleMapper"/>.
/// Raw longs stand in for what real DTOs store as hashids, the same convention as the rest of the scenario.
/// </summary>
public class VehicleListDTO : ShiftEntityDTOBase
{
    public override string? ID { get; set; }
    public string Name { get; set; } = "";
    public long? CompanyID { get; set; }
    public long? IntermediaryCompanyID { get; set; }
}

/// <summary>
/// <see cref="IShiftEntityMapper{TEntity,TListDTO,TViewDTO}"/> double for paths that must not map (the
/// <c>GetIQueryable</c> tests) — every member throws, failing loud if a path unexpectedly maps.
/// </summary>
public sealed class ThrowingVehicleMapper : IShiftEntityMapper<VehicleEntity, VehicleListDTO, VehicleListDTO>
{
    public VehicleListDTO MapToView(VehicleEntity entity, MappingContext context = default) => throw new NotSupportedException();
    public VehicleEntity MapToEntity(VehicleListDTO dto, VehicleEntity existing, MappingContext context = default) => throw new NotSupportedException();
    public IQueryable<VehicleListDTO> MapToList(IQueryable<VehicleEntity> query, MappingContext context = default) => throw new NotSupportedException();
    public void CopyEntity(VehicleEntity source, VehicleEntity target, MappingContext context = default) => throw new NotSupportedException();
}

/// <summary>
/// Mapper for the row-path (upsert) tests: <see cref="MapToEntity"/> applies the DTO's fields onto the entity — so
/// what the repository's Write check sees is the <em>mapped</em> values, the point slice 3.2 pins — and returns the
/// same instance (letting a test hold the reference the check received). Paths the row tests never touch still throw.
/// </summary>
public sealed class UpsertVehicleMapper : IShiftEntityMapper<VehicleEntity, VehicleListDTO, VehicleListDTO>
{
    public VehicleEntity MapToEntity(VehicleListDTO dto, VehicleEntity existing, MappingContext context = default)
    {
        existing.Name = dto.Name;
        existing.CompanyID = dto.CompanyID;
        existing.IntermediaryCompanyID = dto.IntermediaryCompanyID;
        return existing;
    }

    public VehicleListDTO MapToView(VehicleEntity entity, MappingContext context = default) => throw new NotSupportedException();
    public IQueryable<VehicleListDTO> MapToList(IQueryable<VehicleEntity> query, MappingContext context = default) => throw new NotSupportedException();
    public void CopyEntity(VehicleEntity source, VehicleEntity target, MappingContext context = default) => throw new NotSupportedException();
}

/// <summary>
/// Recording <see cref="IDefaultDataLevelAccess"/> double — the legacy arm of the Phase 3 routing tests.
/// <see cref="ApplyDefaultDataLevelFilters{EntityType}"/> (the query path) records the call (and the options it was
/// handed) and applies an unmistakable marker filter (matches nothing), so a test can assert both that the
/// repository routed through the legacy path <em>and</em> that it used the returned query.
/// <see cref="HasDefaultDataLevelAccess{EntityType}"/> (the row path) records the call — count, options, entity
/// (including a null one: legacy row-checks even a missed Find), level — and returns the configurable
/// <see cref="RowCheckVerdict"/> (permissive by default; a test sets <see langword="false"/> to exercise denial).
/// Members the repository never touches throw, to fail loud if a path unexpectedly depends on them.
/// </summary>
public sealed class RecordingDefaultDataLevelAccess : IDefaultDataLevelAccess
{
    public int ApplyFilterCalls { get; private set; }
    public DefaultDataLevelAccessOptions? LastOptions { get; private set; }

    public int RowCheckCalls { get; private set; }
    public DefaultDataLevelAccessOptions? LastRowCheckOptions { get; private set; }
    public object? LastRowCheckEntity { get; private set; }
    public Access? LastRowCheckAccess { get; private set; }

    /// <summary>What <see cref="HasDefaultDataLevelAccess{EntityType}"/> answers (default: permissive).</summary>
    public bool RowCheckVerdict { get; set; } = true;

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
    {
        RowCheckCalls++;
        LastRowCheckOptions = defaultDataLevelAccessOptions;
        LastRowCheckEntity = entity;
        LastRowCheckAccess = access;
        return RowCheckVerdict;
    }

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
