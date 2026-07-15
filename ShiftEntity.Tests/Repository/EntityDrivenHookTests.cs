using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Flags;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.ShiftEntity.Tests.DataLevelAccess.Scenario;
using ShiftSoftware.TypeAuth.Core;
using System.ComponentModel.DataAnnotations.Schema;
using System.Net;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Repository;

/// <summary>
/// The entity-driven repository hooks: an entity can configure the BUILT-IN repository
/// (<see cref="IConfiguresShiftRepository{TEntity, TListDTO, TViewDTO}"/>) and take over its write operations
/// (<see cref="IUpsertsShiftRepository{TEntity, TListDTO, TViewDTO}"/> /
/// <see cref="IDeletesShiftRepository{TEntity, TListDTO, TViewDTO}"/>) without a repository class. All three share
/// <see cref="ShiftRepositoryContext{TEntity, TListDTO, TViewDTO}"/> (Services + Repository); the write hooks add
/// their operands and a <c>Base()</c> that runs the framework default — the analogue of <c>base.UpsertAsync(...)</c>.
/// These tests pin the contract: the hook fires, <c>Base()</c> applies the default, skipping <c>Base()</c> replaces
/// it, and a custom repository subclass is never hooked.
/// </summary>
public class EntityDrivenHookTests
{
    private sealed class HookedListDTO : ShiftEntityDTOBase
    {
        public override string? ID { get; set; }
        public string Name { get; set; } = "";
    }

    /// <summary>
    /// An entity that hooks both write operations. The hooks run on the very instance being upserted/deleted, so
    /// the recording surface is instance state — kept <see cref="NotMappedAttribute"/> so EF doesn't treat it as columns.
    /// </summary>
    private sealed class HookedEntity : ShiftEntity<HookedEntity>,
        IEntityHasIdempotencyKey<HookedEntity>,
        IUpsertsShiftRepository<HookedEntity, HookedListDTO, HookedListDTO>,
        IDeletesShiftRepository<HookedEntity, HookedListDTO, HookedListDTO>
    {
        public string Name { get; set; } = "";

        // The default upsert assigns this whenever the caller passes an idempotency key, so the entity must
        // carry the interface for the key-bearing test below.
        public Guid? IdempotencyKey { get; set; }

        /// <summary>Ordered record of what the hook did — proves the hook ran and where Base() sat in the flow.</summary>
        [NotMapped] public List<string> Trace { get; } = new();

        /// <summary>Set false to make the hook NOT call Base() — i.e. fully replace the default, like an override that omits base.</summary>
        [NotMapped] public bool CallBase { get; set; } = true;

        /// <summary>When set, the hook calls the Base(...) overload with this userId instead of replaying the received args.</summary>
        [NotMapped] public long? OverrideUserId { get; set; }

        [NotMapped] public ShiftRepositoryUpsertContext<HookedEntity, HookedListDTO, HookedListDTO>? UpsertContext { get; private set; }
        [NotMapped] public ShiftRepositoryDeleteContext<HookedEntity, HookedListDTO, HookedListDTO>? DeleteContext { get; private set; }

        // The received parameters, recorded so the tests can pin that the repo's own signature arrives verbatim.
        [NotMapped] public (HookedEntity Entity, HookedListDTO DTO, ActionTypes ActionType, long? UserId, Guid? Key, bool NoDataLevel, bool NoFilters)? UpsertArgs { get; private set; }
        [NotMapped] public (HookedEntity Entity, long? UserId, bool NoDataLevel, bool NoFilters)? DeleteArgs { get; private set; }

        public async ValueTask<HookedEntity> UpsertAsync(
            HookedEntity entity,
            HookedListDTO dto,
            ActionTypes actionType,
            long? userId,
            Guid? idempotencyKey,
            bool disableDefaultDataLevelAccess,
            bool disableGlobalFilters,
            ShiftRepositoryUpsertContext<HookedEntity, HookedListDTO, HookedListDTO> context)
        {
            UpsertContext = context;
            UpsertArgs = (entity, dto, actionType, userId, idempotencyKey, disableDefaultDataLevelAccess, disableGlobalFilters);
            Trace.Add("before");

            if (!CallBase)
            {
                Trace.Add("replaced");
                return entity;
            }

            // Base() replays what we were handed; Base(...) lets us feed the default different arguments.
            var saved = OverrideUserId is null
                ? await context.Base()
                : await context.Base(entity, dto, actionType, OverrideUserId, idempotencyKey,
                    disableDefaultDataLevelAccess, disableGlobalFilters);

            Trace.Add($"after:{saved.Name}");
            return saved;
        }

        public async ValueTask<HookedEntity> DeleteAsync(
            HookedEntity entity,
            long? userId,
            bool disableDefaultDataLevelAccess,
            bool disableGlobalFilters,
            ShiftRepositoryDeleteContext<HookedEntity, HookedListDTO, HookedListDTO> context)
        {
            DeleteContext = context;
            DeleteArgs = (entity, userId, disableDefaultDataLevelAccess, disableGlobalFilters);
            Trace.Add("before");

            if (!CallBase)
            {
                Trace.Add("replaced");
                return entity;
            }

            var deleted = OverrideUserId is null
                ? await context.Base()
                : await context.Base(entity, OverrideUserId, disableDefaultDataLevelAccess, disableGlobalFilters);

            Trace.Add($"after:{deleted.IsDeleted}");
            return deleted;
        }
    }

    /// <summary>An entity that only configures — used to pin that the CONFIG context now carries the repository too.</summary>
    private sealed class ConfiguredEntity : ShiftEntity<ConfiguredEntity>,
        IConfiguresShiftRepository<ConfiguredEntity, HookedListDTO, HookedListDTO>
    {
        public string Name { get; set; } = "";

        // ConfigureRepository runs on a throwaway new() instance the framework creates, so record statically.
        [NotMapped] public static object? LastRepository { get; set; }
        [NotMapped] public static IServiceProvider? LastServices { get; set; }

        public void ConfigureRepository(ShiftRepositoryConfigurationContext<ConfiguredEntity, HookedListDTO, HookedListDTO> context)
        {
            LastRepository = context.Repository;
            LastServices = context.Services;
            context.Options.UseMapper(new ConfiguredMapper());
        }
    }

    private sealed class HookedMapper : IShiftEntityMapper<HookedEntity, HookedListDTO, HookedListDTO>
    {
        public HookedEntity MapToEntity(HookedListDTO dto, HookedEntity existing, MappingContext context = default)
        {
            existing.Name = dto.Name;
            return existing;
        }
        public HookedListDTO MapToView(HookedEntity entity, MappingContext context = default)
            => new() { ID = entity.ID.ToString(), Name = entity.Name };
        public IQueryable<HookedListDTO> MapToList(IQueryable<HookedEntity> query, MappingContext context = default)
            => query.Select(e => new HookedListDTO { ID = e.ID.ToString(), Name = e.Name });
        public void CopyEntity(HookedEntity source, HookedEntity target, MappingContext context = default)
            => target.Name = source.Name;
    }

    private sealed class ConfiguredMapper : IShiftEntityMapper<ConfiguredEntity, HookedListDTO, HookedListDTO>
    {
        public ConfiguredEntity MapToEntity(HookedListDTO dto, ConfiguredEntity existing, MappingContext context = default)
        {
            existing.Name = dto.Name;
            return existing;
        }
        public HookedListDTO MapToView(ConfiguredEntity entity, MappingContext context = default)
            => new() { ID = entity.ID.ToString(), Name = entity.Name };
        public IQueryable<HookedListDTO> MapToList(IQueryable<ConfiguredEntity> query, MappingContext context = default)
            => query.Select(e => new HookedListDTO { ID = e.ID.ToString(), Name = e.Name });
        public void CopyEntity(ConfiguredEntity source, ConfiguredEntity target, MappingContext context = default)
            => target.Name = source.Name;
    }

    private sealed class HookedDbContext : ShiftDbContext
    {
        public DbSet<HookedEntity> Items { get; set; } = default!;
        public DbSet<ConfiguredEntity> Configured { get; set; } = default!;
        public HookedDbContext(DbContextOptions<HookedDbContext> options) : base(options) { }
    }

    /// <summary>A custom repository that does NOT override the write methods — it replaced nothing, so the hook still runs.</summary>
    private sealed class CustomRepoWithoutOverride : ShiftRepository<HookedDbContext, HookedEntity, HookedListDTO, HookedListDTO>
    {
        public CustomRepoWithoutOverride(HookedDbContext db) : base(db, o => o.UseMapper(new HookedMapper())) { }
    }

    /// <summary>A custom repository that overrides and calls base — ordinary inheritance, so the hook runs nested inside it.</summary>
    private sealed class CustomRepoCallingBase : ShiftRepository<HookedDbContext, HookedEntity, HookedListDTO, HookedListDTO>
    {
        public List<string> Trace { get; } = new();

        public CustomRepoCallingBase(HookedDbContext db) : base(db, o => o.UseMapper(new HookedMapper())) { }

        public override async ValueTask<HookedEntity> UpsertAsync(
            HookedEntity entity, HookedListDTO dto, ActionTypes actionType, long? userId, Guid? idempotencyKey,
            bool disableDefaultDataLevelAccess, bool disableGlobalFilters)
        {
            Trace.Add("repo:before");
            var saved = await base.UpsertAsync(entity, dto, actionType, userId, idempotencyKey,
                disableDefaultDataLevelAccess, disableGlobalFilters);
            Trace.Add("repo:after");
            return saved;
        }

        public override async ValueTask<HookedEntity> DeleteAsync(
            HookedEntity entity, long? userId, bool disableDefaultDataLevelAccess, bool disableGlobalFilters)
        {
            Trace.Add("repo:before");
            var deleted = await base.DeleteAsync(entity, userId, disableDefaultDataLevelAccess, disableGlobalFilters);
            Trace.Add("repo:after");
            return deleted;
        }
    }

    /// <summary>
    /// A custom repository over the CONFIGURING entity, passing no options builder. ConfigureRepository must still
    /// not run for it — unlike the write hooks, config is built-in-only.
    /// </summary>
    private sealed class CustomConfiguredRepository : ShiftRepository<HookedDbContext, ConfiguredEntity, HookedListDTO, HookedListDTO>
    {
        public CustomConfiguredRepository(HookedDbContext db) : base(db) { }
    }

    /// <summary>A custom repository that overrides WITHOUT calling base — it replaced the base wholesale, hook included.</summary>
    private sealed class CustomRepoReplacingBase : ShiftRepository<HookedDbContext, HookedEntity, HookedListDTO, HookedListDTO>
    {
        public CustomRepoReplacingBase(HookedDbContext db) : base(db, o => o.UseMapper(new HookedMapper())) { }

        public override ValueTask<HookedEntity> UpsertAsync(
            HookedEntity entity, HookedListDTO dto, ActionTypes actionType, long? userId, Guid? idempotencyKey,
            bool disableDefaultDataLevelAccess, bool disableGlobalFilters)
        {
            entity.Name = "repo-only";
            return new ValueTask<HookedEntity>(entity);
        }

        public override ValueTask<HookedEntity> DeleteAsync(
            HookedEntity entity, long? userId, bool disableDefaultDataLevelAccess, bool disableGlobalFilters)
            => new ValueTask<HookedEntity>(entity);
    }

    private static ServiceProvider BuildHost(RecordingDefaultDataLevelAccess? dataLevelAccess = null)
    {
        var services = new ServiceCollection();

        services.AddDbContext<HookedDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddScoped<ITypeAuthService>(_ => ScopedTypeAuth.ToCompany(Companies.Intermediary));
        services.AddScoped<ICurrentUserProvider>(_ => FakeCurrentUserProvider.Anonymous());
        services.AddScoped<IdentityClaimProvider>();
        services.AddSingleton<IHashIdService>(new RecordingHashIdService());
        services.AddSingleton<IDefaultDataLevelAccess>(dataLevelAccess ?? new RecordingDefaultDataLevelAccess());
        services.AddShiftEntityDataLevelAccess();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    // The BUILT-IN repository (the exact ShiftRepository<,,,> the auto-CRUD path uses) — this is the only path the
    // entity hooks fire on.
    private static ShiftRepository<HookedDbContext, HookedEntity, HookedListDTO, HookedListDTO> BuiltInRepo(IServiceScope scope)
        => new(scope.ServiceProvider.GetRequiredService<HookedDbContext>(), o => o.UseMapper(new HookedMapper()));

    // ---- Upsert hook ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Upsert_EntityHookFires_AndBaseAppliesTheDefault()
    {
        using var provider = BuildHost();
        using var scope = provider.CreateScope();
        var repo = BuiltInRepo(scope);

        var entity = new HookedEntity();

        var result = await repo.UpsertAsync(entity, new HookedListDTO { Name = "mapped" }, ActionTypes.Insert,
            userId: null, idempotencyKey: null, disableDefaultDataLevelAccess: true, disableGlobalFilters: true);

        // The hook ran, and Base() performed the default mapping in the middle of it.
        Assert.Equal(new[] { "before", "after:mapped" }, entity.Trace);
        Assert.Equal("mapped", result.Name);
        Assert.Same(entity, result);
    }

    [Fact]
    public async Task Upsert_HookThatSkipsBase_ReplacesTheDefaultEntirely()
    {
        using var provider = BuildHost();
        using var scope = provider.CreateScope();
        var repo = BuiltInRepo(scope);

        var entity = new HookedEntity { Name = "untouched", CallBase = false };

        var result = await repo.UpsertAsync(entity, new HookedListDTO { Name = "mapped" }, ActionTypes.Insert,
            userId: null, idempotencyKey: null, disableDefaultDataLevelAccess: true, disableGlobalFilters: true);

        Assert.Equal(new[] { "before", "replaced" }, entity.Trace);
        Assert.Equal("untouched", result.Name);   // MapToEntity never ran — the default was fully replaced
    }

    [Fact]
    public async Task Upsert_HookReceivesTheRepositorySignatureVerbatim_PlusContextExtras()
    {
        using var provider = BuildHost();
        using var scope = provider.CreateScope();
        var repo = BuiltInRepo(scope);

        var entity = new HookedEntity();
        var dto = new HookedListDTO { Name = "x" };
        var key = Guid.NewGuid();

        await repo.UpsertAsync(entity, dto, ActionTypes.Insert, userId: 42, idempotencyKey: key,
            disableDefaultDataLevelAccess: true, disableGlobalFilters: false);

        // Every parameter of ShiftRepository.UpsertAsync reaches the hook, unchanged and in order.
        var args = entity.UpsertArgs!.Value;
        Assert.Same(entity, args.Entity);
        Assert.Same(dto, args.DTO);
        Assert.Equal(ActionTypes.Insert, args.ActionType);
        Assert.Equal(42, args.UserId);
        Assert.Equal(key, args.Key);
        Assert.True(args.NoDataLevel);
        Assert.False(args.NoFilters);

        Assert.Equal(key, entity.IdempotencyKey);   // Base() applied it, proving the key reached the default

        // The context carries only the extras: the request scope and the repository serving the call.
        Assert.Same(repo, entity.UpsertContext!.Repository);
        Assert.NotNull(entity.UpsertContext!.Services.GetService<ICurrentUserProvider>());
    }

    [Fact]
    public async Task Upsert_HookReceivesUpdateActionType()
    {
        using var provider = BuildHost();
        using var scope = provider.CreateScope();
        var repo = BuiltInRepo(scope);

        var entity = new HookedEntity { Name = "existing" };

        await repo.UpsertAsync(entity, new HookedListDTO { Name = "edited" }, ActionTypes.Update,
            userId: null, idempotencyKey: null, disableDefaultDataLevelAccess: true, disableGlobalFilters: true);

        Assert.Equal(ActionTypes.Update, entity.UpsertArgs!.Value.ActionType);
        Assert.Equal("edited", entity.Name);
    }

    [Fact]
    public async Task Upsert_BaseOverload_RunsTheDefaultWithModifiedArguments()
    {
        using var provider = BuildHost();
        using var scope = provider.CreateScope();
        var repo = BuiltInRepo(scope);

        // The hook is handed userId 1 but feeds the default 99 through the Base(...) overload — the analogue of
        // calling base.UpsertAsync(...) with changed arguments.
        var entity = new HookedEntity { OverrideUserId = 99 };

        await repo.UpsertAsync(entity, new HookedListDTO { Name = "x" }, ActionTypes.Insert,
            userId: 1, idempotencyKey: null, disableDefaultDataLevelAccess: true, disableGlobalFilters: true);

        Assert.Equal(1, entity.UpsertArgs!.Value.UserId);   // what the repository passed in
        Assert.Equal(99, entity.LastSavedByUserID);          // what the hook actually gave the default
    }

    // ---- Delete hook ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_EntityHookFires_AndBaseSoftDeletes()
    {
        using var provider = BuildHost();
        using var scope = provider.CreateScope();
        var repo = BuiltInRepo(scope);

        var entity = new HookedEntity { Name = "row" };

        var result = await repo.DeleteAsync(entity, userId: null,
            disableDefaultDataLevelAccess: true, disableGlobalFilters: true);

        Assert.Equal(new[] { "before", "after:True" }, entity.Trace);
        Assert.True(result.IsDeleted);
    }

    [Fact]
    public async Task Delete_HookThatSkipsBase_ReplacesTheDefaultEntirely()
    {
        using var provider = BuildHost();
        using var scope = provider.CreateScope();
        var repo = BuiltInRepo(scope);

        var entity = new HookedEntity { Name = "row", CallBase = false };

        var result = await repo.DeleteAsync(entity, userId: null,
            disableDefaultDataLevelAccess: true, disableGlobalFilters: true);

        Assert.Equal(new[] { "before", "replaced" }, entity.Trace);
        Assert.False(result.IsDeleted);   // never soft-deleted — the default was fully replaced
    }

    [Fact]
    public async Task Delete_HookReceivesTheRepositorySignatureVerbatim_PlusContextExtras()
    {
        using var provider = BuildHost();
        using var scope = provider.CreateScope();
        var repo = BuiltInRepo(scope);

        var entity = new HookedEntity { Name = "row" };

        await repo.DeleteAsync(entity, userId: 7, disableDefaultDataLevelAccess: true, disableGlobalFilters: false);

        // Every parameter of ShiftRepository.DeleteAsync reaches the hook, unchanged and in order.
        var args = entity.DeleteArgs!.Value;
        Assert.Same(entity, args.Entity);
        Assert.Equal(7, args.UserId);
        Assert.True(args.NoDataLevel);
        Assert.False(args.NoFilters);

        // The context carries only the extras.
        Assert.Same(repo, entity.DeleteContext!.Repository);
        Assert.NotNull(entity.DeleteContext!.Services.GetService<ICurrentUserProvider>());
    }

    [Fact]
    public async Task Delete_BaseOverload_RunsTheDefaultWithModifiedArguments()
    {
        using var provider = BuildHost();
        using var scope = provider.CreateScope();
        var repo = BuiltInRepo(scope);

        var entity = new HookedEntity { Name = "row", OverrideUserId = 99 };

        await repo.DeleteAsync(entity, userId: 1, disableDefaultDataLevelAccess: true, disableGlobalFilters: true);

        Assert.Equal(1, entity.DeleteArgs!.Value.UserId);   // what the repository passed in
        Assert.Equal(99, entity.LastSavedByUserID);          // what the hook actually gave the default
        Assert.True(entity.IsDeleted);
    }

    // ---- Base() forwards the bypass flags (regression: they are two adjacent bools, trivially transposable) ----

    // The production shape: ShiftEntityCrudHandler calls with RepositoryBypass.None, i.e.
    // disableDefaultDataLevelAccess: false — nothing bypassed. Base() must forward that flag UNCHANGED so the
    // default's Write check still runs and can still deny. Every other hook test passes `true` here (bypassing the
    // check), which makes them blind to a transposition of the two adjacent bool arguments inside Base(); this test
    // is what pins the order. Swapping them makes it fail.
    [Fact]
    public async Task Upsert_Base_ForwardsBypassFlagsUnchanged_SoTheWriteCheckStillRuns()
    {
        var dataLevel = new RecordingDefaultDataLevelAccess { RowCheckVerdict = false };
        using var provider = BuildHost(dataLevel);
        using var scope = provider.CreateScope();
        var repo = BuiltInRepo(scope);

        var entity = new HookedEntity();

        // The asymmetric pair (don't bypass the check, DO bypass the filters) is what makes a swap observable.
        var ex = await Assert.ThrowsAsync<ShiftEntityException>(async () =>
            await repo.UpsertAsync(entity, new HookedListDTO { Name = "x" }, ActionTypes.Insert,
                userId: null, idempotencyKey: null,
                disableDefaultDataLevelAccess: false, disableGlobalFilters: true));

        Assert.Equal((int)HttpStatusCode.Forbidden, ex.HttpStatusCode);
        Assert.Equal(ShiftSoftware.TypeAuth.Core.Access.Write, dataLevel.LastRowCheckAccess);
        Assert.Equal(new[] { "before" }, entity.Trace);   // the hook ran; Base() threw before it could resume
    }

    [Fact]
    public async Task Delete_Base_ForwardsBypassFlagsUnchanged_SoTheDeleteCheckStillRuns()
    {
        var dataLevel = new RecordingDefaultDataLevelAccess { RowCheckVerdict = false };
        using var provider = BuildHost(dataLevel);
        using var scope = provider.CreateScope();
        var repo = BuiltInRepo(scope);

        var entity = new HookedEntity { Name = "row" };

        var ex = await Assert.ThrowsAsync<ShiftEntityException>(async () =>
            await repo.DeleteAsync(entity, userId: null,
                disableDefaultDataLevelAccess: false, disableGlobalFilters: true));

        Assert.Equal((int)HttpStatusCode.Forbidden, ex.HttpStatusCode);
        Assert.Equal(ShiftSoftware.TypeAuth.Core.Access.Delete, dataLevel.LastRowCheckAccess);
        Assert.False(entity.IsDeleted);   // denied before the soft-delete flag was touched
    }

    // The mirror: replacing the default also replaces its authorization — the hook owns that decision, exactly
    // like an override that never calls base. Pins that the check is reached ONLY through Base().
    [Fact]
    public async Task Upsert_HookThatSkipsBase_NeverRunsTheWriteCheck()
    {
        var dataLevel = new RecordingDefaultDataLevelAccess { RowCheckVerdict = false };
        using var provider = BuildHost(dataLevel);
        using var scope = provider.CreateScope();
        var repo = BuiltInRepo(scope);

        var entity = new HookedEntity { CallBase = false };

        await repo.UpsertAsync(entity, new HookedListDTO { Name = "x" }, ActionTypes.Insert,
            userId: null, idempotencyKey: null,
            disableDefaultDataLevelAccess: false, disableGlobalFilters: false);

        Assert.Equal(0, dataLevel.RowCheckCalls);   // never consulted — no Base(), no default, no check
    }

    // ---- Custom repositories: the hook is part of UpsertAsync/DeleteAsync, so override semantics decide --------

    // Case 1: a repository class exists but overrides nothing. The hook lives in ShiftRepository.UpsertAsync, which
    // nothing replaced, so it still runs. This is the case the old built-in-only guard got wrong: adding a repo for
    // an unrelated reason (an Include, say) silently switched the entity's declared behavior off.
    [Fact]
    public async Task CustomRepository_WithoutOverride_StillFiresTheHook()
    {
        using var provider = BuildHost();
        using var scope = provider.CreateScope();
        var repo = new CustomRepoWithoutOverride(scope.ServiceProvider.GetRequiredService<HookedDbContext>());

        var entity = new HookedEntity();

        var upserted = await repo.UpsertAsync(entity, new HookedListDTO { Name = "mapped" }, ActionTypes.Insert,
            userId: null, idempotencyKey: null, disableDefaultDataLevelAccess: true, disableGlobalFilters: true);

        Assert.Equal(new[] { "before", "after:mapped" }, entity.Trace);
        Assert.Equal("mapped", upserted.Name);
    }

    [Fact]
    public async Task CustomRepository_WithoutOverride_StillFiresTheDeleteHook()
    {
        using var provider = BuildHost();
        using var scope = provider.CreateScope();
        var repo = new CustomRepoWithoutOverride(scope.ServiceProvider.GetRequiredService<HookedDbContext>());

        var entity = new HookedEntity { Name = "row" };

        await repo.DeleteAsync(entity, userId: null, disableDefaultDataLevelAccess: true, disableGlobalFilters: true);

        Assert.Equal(new[] { "before", "after:True" }, entity.Trace);
        Assert.True(entity.IsDeleted);
    }

    // Case 2: overrides and calls base. Calling base means "run the base's behavior", and the hook IS part of that —
    // so both layers run, repository outermost, then the entity, then the default.
    [Fact]
    public async Task CustomRepository_OverridingAndCallingBase_FiresTheHookNestedInside()
    {
        using var provider = BuildHost();
        using var scope = provider.CreateScope();
        var repo = new CustomRepoCallingBase(scope.ServiceProvider.GetRequiredService<HookedDbContext>());

        var entity = new HookedEntity();

        var upserted = await repo.UpsertAsync(entity, new HookedListDTO { Name = "mapped" }, ActionTypes.Insert,
            userId: null, idempotencyKey: null, disableDefaultDataLevelAccess: true, disableGlobalFilters: true);

        Assert.Equal(new[] { "repo:before", "repo:after" }, repo.Trace);      // the repository wraps
        Assert.Equal(new[] { "before", "after:mapped" }, entity.Trace);       // the entity ran inside it
        Assert.Equal("mapped", upserted.Name);                               // and the default ran inside that
    }

    [Fact]
    public async Task CustomRepository_OverridingAndCallingBase_FiresTheDeleteHookNestedInside()
    {
        using var provider = BuildHost();
        using var scope = provider.CreateScope();
        var repo = new CustomRepoCallingBase(scope.ServiceProvider.GetRequiredService<HookedDbContext>());

        var entity = new HookedEntity { Name = "row" };

        await repo.DeleteAsync(entity, userId: null, disableDefaultDataLevelAccess: true, disableGlobalFilters: true);

        Assert.Equal(new[] { "repo:before", "repo:after" }, repo.Trace);
        Assert.Equal(new[] { "before", "after:True" }, entity.Trace);
        Assert.True(entity.IsDeleted);
    }

    // Case 3: overrides WITHOUT calling base. The base — hook and default alike — was replaced wholesale, exactly as
    // an override that omits base always behaves. Nothing entity-side runs.
    [Fact]
    public async Task CustomRepository_OverridingWithoutCallingBase_DoesNotFireTheHook()
    {
        using var provider = BuildHost();
        using var scope = provider.CreateScope();
        var repo = new CustomRepoReplacingBase(scope.ServiceProvider.GetRequiredService<HookedDbContext>());

        var entity = new HookedEntity();

        var upserted = await repo.UpsertAsync(entity, new HookedListDTO { Name = "mapped" }, ActionTypes.Insert,
            userId: null, idempotencyKey: null, disableDefaultDataLevelAccess: true, disableGlobalFilters: true);
        await repo.DeleteAsync(entity, userId: null, disableDefaultDataLevelAccess: true, disableGlobalFilters: true);

        Assert.Empty(entity.Trace);                  // neither hook fired
        Assert.Equal("repo-only", upserted.Name);    // the repository's own behavior stands alone
        Assert.False(entity.IsDeleted);              // the default never ran either
    }

    // ---- Configure stays built-in-only ------------------------------------------------------------------------

    // The write hooks now fire for custom repositories, but ConfigureRepository deliberately does not: a custom
    // repository configures itself through the options builder it passes to the base constructor, and the two are
    // alternatives rather than layers. Pins that asymmetry so it can't drift by accident.
    [Fact]
    public void Configure_IsNotCalled_ForACustomRepositorySubclass()
    {
        using var provider = BuildHost();
        using var scope = provider.CreateScope();
        ConfiguredEntity.LastRepository = null;

        _ = new CustomConfiguredRepository(scope.ServiceProvider.GetRequiredService<HookedDbContext>());

        Assert.Null(ConfiguredEntity.LastRepository);
    }

    // ---- Configure hook now carries the repository too --------------------------------------------------------

    [Fact]
    public void Configure_Context_CarriesTheRepositoryInstance_AndServices()
    {
        using var provider = BuildHost();
        using var scope = provider.CreateScope();
        ConfiguredEntity.LastRepository = null;
        ConfiguredEntity.LastServices = null;

        // Constructing the built-in repository is what invokes the entity's ConfigureRepository.
        var repo = new ShiftRepository<HookedDbContext, ConfiguredEntity, HookedListDTO, HookedListDTO>(
            scope.ServiceProvider.GetRequiredService<HookedDbContext>());

        Assert.Same(repo, ConfiguredEntity.LastRepository);
        Assert.NotNull(ConfiguredEntity.LastServices);
    }
}
