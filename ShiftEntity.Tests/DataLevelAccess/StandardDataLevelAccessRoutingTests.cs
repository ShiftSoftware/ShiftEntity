using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.DataLevelAccess;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Tests.DataLevelAccess.Scenario;
using ShiftSoftware.ShiftEntity.Web.Services;
using ShiftSoftware.ShiftIdentity.Core;
using ShiftSoftware.ShiftIdentity.Core.DTOs.Company;
using ShiftSoftware.TypeAuth.Core;
using ShiftSoftware.TypeAuth.Core.Actions;
using Xunit;
using DataLevelClaims = ShiftSoftware.ShiftEntity.Core.Constants;

namespace ShiftSoftware.ShiftEntity.Tests.DataLevelAccess;

/// <summary>
/// Phase 4.8 — automatic standard-marker routing and explicit override composition. The host opts into the standard
/// provider once; repositories then resolve their effective policy lazily. Explicit dimensions replace the marker
/// dimension for the same action (preventing the Company AND-trap) while unrelated markers remain automatic.
/// </summary>
public class StandardDataLevelAccessRoutingTests
{
    private static ShiftRepository<VehicleDbContext, VehicleEntity, VehicleListDTO, VehicleListDTO> Repository(
        VehicleDbContext db,
        Action<ShiftRepositoryOptions<VehicleEntity, VehicleListDTO, VehicleListDTO>>? configure = null)
        => new(db, options =>
        {
            options.UseMapper(new ThrowingVehicleMapper());
            configure?.Invoke(options);
        });

    private static async Task<List<long>> VisibleIds(
        ShiftRepository<VehicleDbContext, VehicleEntity, VehicleListDTO, VehicleListDTO> repository)
    {
        var query = await repository.GetIQueryable(
            asOf: null, includes: null,
            disableDefaultDataLevelAccess: false, disableGlobalFilters: false);
        return query.Select(vehicle => vehicle.ID).OrderBy(id => id).ToList();
    }

    private static ShiftRepository<VehicleDbContext, VehicleEntity, VehicleListDTO, VehicleListDTO> MarkerOnlyRowRepository(
        VehicleDbContext db)
        => new(db, options =>
        {
            options.UseMapper(new UpsertVehicleMapper());
            options.DefaultDataLevelAccessOptions = new DefaultDataLevelAccessOptions
            {
                // VehicleEntity carries Country and Company markers. Isolate Company so the DTO used by the row
                // tests only needs to carry the company value that the effective profile authorizes.
                DisableDefaultCountryFilter = true,
            };
        });

    private static async Task<ShiftEntityException> AssertForbidden(string body, Func<Task> operation)
    {
        var exception = await Assert.ThrowsAsync<ShiftEntityException>(operation);
        Assert.Equal(403, exception.HttpStatusCode);
        Assert.Equal(body, exception.Message.Body);
        return exception;
    }

    private static ITypeAuthService StandardAccess()
        => IdentityScopedTypeAuth.ToIdsOnActions(new Dictionary<string, IEnumerable<long>>
        {
            [nameof(ShiftIdentityActions.DataLevelAccess.Companies)] = new long[] { 4 },
            [nameof(ShiftIdentityActions.DataLevelAccess.Countries)] = new long[] { 8 },
        });

    [Fact]
    public async Task OptedInHost_MarkerOnlyRepository_RoutesThroughStandardV2Policy()
    {
        var legacy = new RecordingDefaultDataLevelAccess();
        using var provider = RepositoryHost.Build(
            typeAuth: StandardAccess,
            legacy: legacy,
            withHostDataLevelAccess: true);
        using var scope = provider.CreateScope();

        var repository = Repository(RepositoryHost.SeededDb(scope));

        // Standard Company is the legacy-compatible single CompanyID leg, so only canonical row #3 matches.
        Assert.Equal(new long[] { 3 }, await VisibleIds(repository));
        Assert.NotNull(repository.DataLevelAccessPolicy);
        Assert.Equal(0, legacy.ApplyFilterCalls);
    }

    [Fact]
    public async Task OptedInHost_MarkerOnlyWrite_UsesEffectiveProfileAndNeverLegacyRowCheck()
    {
        // A false legacy verdict makes accidental fallback fail loudly: the in-scope insert can pass only through
        // the opted-in marker profile. The out-of-scope insert then anchors that the effective profile really
        // enforces Write rather than merely bypassing both engines.
        var legacy = new RecordingDefaultDataLevelAccess { RowCheckVerdict = false };
        using var provider = RepositoryHost.Build(
            typeAuth: () => IdentityScopedTypeAuth.ToId(
                nameof(ShiftIdentityActions.DataLevelAccess.Companies), 4, Access.Write),
            legacy: legacy,
            withHostDataLevelAccess: true);
        using var scope = provider.CreateScope();

        var repository = MarkerOnlyRowRepository(RepositoryHost.SeededDb(scope));

        var allowed = await repository.UpsertAsync(
            new VehicleEntity(),
            new VehicleListDTO { Name = "allowed", CompanyID = 4 },
            ActionTypes.Insert,
            userId: null,
            idempotencyKey: null,
            disableDefaultDataLevelAccess: false,
            disableGlobalFilters: false);

        Assert.Equal(4, allowed.CompanyID);
        Assert.NotNull(repository.DataLevelAccessPolicy);

        await AssertForbidden("Can Not Create Item", () => repository.UpsertAsync(
            new VehicleEntity(),
            new VehicleListDTO { Name = "denied", CompanyID = 1 },
            ActionTypes.Insert,
            userId: null,
            idempotencyKey: null,
            disableDefaultDataLevelAccess: false,
            disableGlobalFilters: false).AsTask());

        Assert.Equal(0, legacy.RowCheckCalls);
    }

    [Fact]
    public async Task OptedInHost_MarkerOnlyDelete_UsesEffectiveProfileAndNeverLegacyRowCheck()
    {
        // Delete has its own TypeAuth level. Company 4 is soft-deleted through the profile; Company 1 is refused
        // before mutation. The false legacy verdict again proves neither result came from the legacy row arm.
        var legacy = new RecordingDefaultDataLevelAccess { RowCheckVerdict = false };
        using var provider = RepositoryHost.Build(
            typeAuth: () => IdentityScopedTypeAuth.ToId(
                nameof(ShiftIdentityActions.DataLevelAccess.Companies), 4, Access.Delete),
            legacy: legacy,
            withHostDataLevelAccess: true);
        using var scope = provider.CreateScope();

        var repository = MarkerOnlyRowRepository(RepositoryHost.SeededDb(scope));
        var allowed = VehicleEntity.FromSamples().Single(vehicle => vehicle.ID == 3);
        var denied = VehicleEntity.FromSamples().Single(vehicle => vehicle.ID == 1);

        var deleted = await repository.DeleteAsync(
            allowed,
            userId: null,
            disableDefaultDataLevelAccess: false,
            disableGlobalFilters: false);

        Assert.True(deleted.IsDeleted);

        await AssertForbidden("Can Not Delete Item", () => repository.DeleteAsync(
            denied,
            userId: null,
            disableDefaultDataLevelAccess: false,
            disableGlobalFilters: false).AsTask());

        Assert.False(denied.IsDeleted);
        Assert.NotNull(repository.DataLevelAccessPolicy);
        Assert.Equal(0, legacy.RowCheckCalls);
    }

    [Fact]
    public async Task ExplicitSameAction_ReplacesStandardDimension_InsteadOfAndComposing()
    {
        var legacy = new RecordingDefaultDataLevelAccess();
        using var provider = RepositoryHost.Build(
            typeAuth: StandardAccess,
            legacy: legacy,
            withHostDataLevelAccess: true);
        using var scope = provider.CreateScope();

        var repository = Repository(RepositoryHost.SeededDb(scope), options =>
            options.DataLevelAccess(access =>
                access.On(ShiftIdentityActions.DataLevelAccess.Companies)
                    .Keys(vehicle => vehicle.CompanyID, vehicle => vehicle.IntermediaryCompanyID)
                    .HashId<CompanyDTO>()
                    .Self(DataLevelClaims.CompanyIdClaim)));

        // If automatic CompanyID were appended instead of replaced, this would collapse back to {3}.
        Assert.Equal(new long[] { 3, 4, 5, 6 }, await VisibleIds(repository));
        Assert.Equal(0, legacy.ApplyFilterCalls);
    }

    [Fact]
    public async Task ExplicitUnscoped_WinsOverAutomaticMarkers()
    {
        var legacy = new RecordingDefaultDataLevelAccess();
        using var provider = RepositoryHost.Build(
            typeAuth: StandardAccess,
            legacy: legacy,
            withHostDataLevelAccess: true);
        using var scope = provider.CreateScope();

        var repository = Repository(
            RepositoryHost.SeededDb(scope),
            options => options.DataLevelAccess(access => access.Unscoped()));

        Assert.Equal(new long[] { 1, 2, 3, 4, 5, 6, 7 }, await VisibleIds(repository));
        Assert.True(repository.DataLevelAccessPolicy!.IsUnscoped);
        Assert.Equal(0, legacy.ApplyFilterCalls);
    }

    [Fact]
    public void EffectivePolicy_IsResolvedAfterDerivedConstructorFinalizesFlags()
    {
        using var provider = RepositoryHost.Build(
            typeAuth: StandardAccess,
            withHostDataLevelAccess: true);
        using var scope = provider.CreateScope();

        var repository = new CompanyDisabledVehicleRepository(RepositoryHost.SeededDb(scope));

        // The derived ctor disables both VehicleEntity markers after base construction, so the profile must be
        // empty and return null rather than caching either dimension during InitCommon.
        Assert.Null(repository.DataLevelAccessPolicy);
    }

    [Fact]
    public void Profile_WhenDeniedDeclaration_IsRejectedBeforeRepositoryOverrides()
    {
        using var provider = RepositoryHost.Build(registerBeforeDataLevelAccess: services =>
            services.AddSingleton<IDataLevelAccessProfile<VehicleEntity>, DeniedBehaviorProfile>());
        using var scope = provider.CreateScope();

        var repository = Repository(RepositoryHost.SeededDb(scope), options =>
            options.DataLevelAccess(access =>
            {
                access.On(VehicleDataLevel.Companies).Key(vehicle => vehicle.CompanyID);
                access.WhenDenied(DataLevelDeniedBehavior.NotFound);
            }));

        var exception = Assert.Throws<InvalidOperationException>(() => _ = repository.DataLevelAccessPolicy);
        Assert.Contains("called WhenDenied(...)", exception.Message);
        Assert.Contains("repository declaration", exception.Message);
    }

    [Fact]
    public async Task CustomOpenGenericProfile_RegisteredFirstWinsStandardOptIn_AndRunsLazilyOnce()
    {
        var legacy = new RecordingDefaultDataLevelAccess();
        using var provider = RepositoryHost.Build(
            typeAuth: () => IdentityScopedTypeAuth.ToId(
                nameof(ShiftIdentityActions.DataLevelAccess.Companies), 4),
            legacy: legacy,
            withHostDataLevelAccess: true,
            registerBeforeDataLevelAccess: services => services.AddSingleton(
                typeof(IDataLevelAccessProfile<>),
                typeof(CountingCompanyOrProfile<>)));
        using var scope = provider.CreateScope();

        var registered = Assert.IsType<CountingCompanyOrProfile<VehicleEntity>>(
            scope.ServiceProvider.GetRequiredService<IDataLevelAccessProfile<VehicleEntity>>());
        var repository = Repository(RepositoryHost.SeededDb(scope));

        Assert.Equal(0, registered.InvocationCount); // repository construction is still lazy

        var first = repository.DataLevelAccessPolicy;
        Assert.NotNull(first);
        Assert.Equal(1, registered.InvocationCount);
        Assert.Same(first, repository.DataLevelAccessPolicy);
        Assert.Equal(1, registered.InvocationCount);

        // The custom profile's Company OR is observably different from the standard profile's single CompanyID
        // marker (and it contributes no Country dimension): it admits the owner and all intermediary-leg rows.
        Assert.Equal(new long[] { 3, 4, 5, 6 }, await VisibleIds(repository));
        Assert.Equal(1, registered.InvocationCount);
        Assert.Equal(0, legacy.ApplyFilterCalls);
        Assert.Equal(0, legacy.RowCheckCalls);
    }

    [Fact]
    public async Task ExplicitCompanyOverride_LeavesUnrelatedCountryMarkerAutomatic()
    {
        using var provider = RepositoryHost.Build(
            typeAuth: StandardAccess,
            withHostDataLevelAccess: true);
        using var scope = provider.CreateScope();

        var db = RepositoryHost.SeededDb(scope);
        db.Vehicles.Single(vehicle => vehicle.ID == 4).CountryID = 9;
        db.SaveChanges();

        var repository = Repository(db, options =>
            options.DataLevelAccess(access =>
                access.On(ShiftIdentityActions.DataLevelAccess.Companies)
                    .Keys(vehicle => vehicle.CompanyID, vehicle => vehicle.IntermediaryCompanyID)
                    .HashId<CompanyDTO>()));

        // The explicit Company OR admits #3-#6, then the untouched automatic Country dimension removes #4.
        Assert.Equal(new long[] { 3, 5, 6 }, await VisibleIds(repository));
    }

    [Fact]
    public async Task EquivalentActionInstance_WithTheSamePath_ReplacesTheStandardDimension()
    {
        var typeAuth = StandardAccess(); // populates the static action-tree paths
        var equivalentCompanies = new DynamicReadWriteDeleteAction("Equivalent Companies")
        {
            Path = ShiftIdentityActions.DataLevelAccess.Companies.Path,
        };
        Assert.NotNull(equivalentCompanies.Path);

        using var provider = RepositoryHost.Build(
            typeAuth: () => typeAuth,
            withHostDataLevelAccess: true);
        using var scope = provider.CreateScope();

        var repository = Repository(RepositoryHost.SeededDb(scope), options =>
            options.DataLevelAccess(access =>
                access.On(equivalentCompanies)
                    .Keys(vehicle => vehicle.CompanyID, vehicle => vehicle.IntermediaryCompanyID)
                    .HashId<CompanyDTO>()));

        Assert.Equal(new long[] { 3, 4, 5, 6 }, await VisibleIds(repository));
    }

    [Fact]
    public void NoMarkersAndNoDeclaration_ProducesNoPolicy()
    {
        var profile = new DataLevelAccessBuilder<UnmarkedEntity>();
        new StandardDataLevelAccessProfileProvider<UnmarkedEntity>()
            .AddDimensions(profile, new DefaultDataLevelAccessOptions());

        Assert.Empty(profile.Dimensions);
    }

    private sealed class CompanyDisabledVehicleRepository
        : ShiftRepository<VehicleDbContext, VehicleEntity, VehicleListDTO, VehicleListDTO>
    {
        public CompanyDisabledVehicleRepository(VehicleDbContext db)
            : base(db, options => options.UseMapper(new ThrowingVehicleMapper()))
        {
            ShiftRepositoryOptions.DefaultDataLevelAccessOptions = new DefaultDataLevelAccessOptions
            {
                DisableDefaultCountryFilter = true,
                DisableDefaultCompanyFilter = true,
            };
        }
    }

    private sealed class UnmarkedEntity : ShiftEntity<UnmarkedEntity> { }

    private sealed class DeniedBehaviorProfile : IDataLevelAccessProfile<VehicleEntity>
    {
        public void AddDimensions(
            DataLevelAccessBuilder<VehicleEntity> builder,
            DefaultDataLevelAccessOptions options)
            => builder.WhenDenied(DataLevelDeniedBehavior.Forbidden);
    }

    private sealed class CountingCompanyOrProfile<TEntity> : IDataLevelAccessProfile<TEntity>
    {
        public int InvocationCount { get; private set; }

        public void AddDimensions(DataLevelAccessBuilder<TEntity> builder, DefaultDataLevelAccessOptions options)
        {
            InvocationCount++;

            if (typeof(TEntity) != typeof(VehicleEntity))
                return;

            var vehicleBuilder = (DataLevelAccessBuilder<VehicleEntity>)(object)builder;
            vehicleBuilder.On(ShiftIdentityActions.DataLevelAccess.Companies)
                .Keys(vehicle => vehicle.CompanyID, vehicle => vehicle.IntermediaryCompanyID)
                .HashId<CompanyDTO>();
        }
    }
}
