using AutoMapper;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Attention;
using ShiftSoftware.ShiftEntity.Core.DataLevelAccess;
using ShiftSoftware.ShiftEntity.Core.Flags;
using ShiftSoftware.ShiftEntity.Core.Tagging;
using ShiftSoftware.ShiftEntity.EFCore.Attention;
using ShiftSoftware.ShiftEntity.EFCore.Tagging;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.ShiftEntity.Model.Dtos.Tagging;
using ShiftSoftware.ShiftEntity.Model.Flags;
using ShiftSoftware.TypeAuth.Core;
using System.Net;
using System.Security.Claims;
using System.Text;

namespace ShiftSoftware.ShiftEntity.EFCore;

public class ShiftRepository<DB, EntityType, ListDTO, ViewAndUpsertDTO> :
    ShiftRepositoryBase,
    IShiftRepositoryAsync<EntityType, ListDTO, ViewAndUpsertDTO>,
    IShiftRepositoryWithOptions<EntityType, ListDTO, ViewAndUpsertDTO>,
    IShiftEntityMapper<EntityType, ListDTO, ViewAndUpsertDTO>
    where DB : ShiftDbContext
    where EntityType : ShiftEntity<EntityType>, new()
    where ListDTO : ShiftEntityDTOBase
{
    public DB db { get; private set; } = default!;
    internal DbSet<EntityType> dbSet = default!;
    public override object? GetDbContext() => db;

    // The default/plugged mapper that this repository's own (virtual) mapping methods delegate to.
    // Defaults to an AutoMapper-backed mapper; replaced by UseMapper(...) when a custom one is plugged.
    protected IShiftEntityMapper<EntityType, ListDTO, ViewAndUpsertDTO>? innerMapper { get; private set; }
    public ShiftRepositoryOptions<EntityType, ListDTO, ViewAndUpsertDTO> ShiftRepositoryOptions { get; set; } = default!;
    public IDefaultDataLevelAccess? defaultDataLevelAccess { get; private set; }
    public IdentityClaimProvider identityClaimProvider { get; private set; } = default!;
    public ICurrentUserProvider? currentUserProvider { get; private set; }
    private bool _hasUniqueHashInterface;
    private bool _hasAttentionInterface;
    private bool _needsAttentionTransaction;
    private bool _hasTaggableInterface;

    private IShiftEntityHasBeforeSaveHook<EntityType>? beforeSaveHook = null;
    private IShiftEntityHasAfterSaveHook<EntityType>? afterSaveHook = null;

    public ShiftRepository(DB db, Action<ShiftRepositoryOptions<EntityType, ListDTO, ViewAndUpsertDTO>>? shiftRepositoryBuilder = null)
    {
        InitCommon(db, shiftRepositoryBuilder);
    }

    // Optional service resolution: db.GetService<T>() throws when a service isn't registered, so this
    // wraps it to return null instead. It uses EF's resolution (which also checks the application
    // service provider, where AutoMapper / TypeAuth / HashId are typically registered).
    private static TService? TryGetService<TService>(DB db) where TService : class
    {
        try { return db.GetService<TService>(); }
        catch (InvalidOperationException) { return null; }
    }

    private void InitCommon(DB db, Action<ShiftRepositoryOptions<EntityType, ListDTO, ViewAndUpsertDTO>>? shiftRepositoryBuilder)
    {
        this.db = db;
        this.dbSet = db.Set<EntityType>();
        this.identityClaimProvider = db.GetService<IdentityClaimProvider>();
        this.currentUserProvider = db.GetService<ICurrentUserProvider>();
        this.ShiftRepositoryOptions = new();

        // Baseline the data-level-access options from a host-registered default when one exists (e.g. ShiftIdentity
        // registers its ShiftIdentityDefaultDataLevelAccessOptions). A custom repository that assigns
        // ShiftRepositoryOptions.DefaultDataLevelAccessOptions in its constructor body still wins — that runs after
        // this base constructor. The value here matters for the built-in / auto-CRUD repository path, which has no
        // such constructor: without this it would fall back to a new() (all default filters enabled) instead of the
        // host's configured default. Absent a registration this resolves to null and the new() default stands, so
        // consumers that don't register one are unaffected.
        var registeredDefaultDataLevelAccessOptions = TryGetService<DefaultDataLevelAccessOptions>(db);
        if (registeredDefaultDataLevelAccessOptions is not null)
            this.ShiftRepositoryOptions.DefaultDataLevelAccessOptions = registeredDefaultDataLevelAccessOptions;

        _hasUniqueHashInterface = typeof(EntityType).GetInterfaces()
            .Any(x => x.IsAssignableFrom(typeof(IEntityHasUniqueHash<EntityType>)));

        _hasAttentionInterface = typeof(IHasAttention).IsAssignableFrom(typeof(EntityType));
        _needsAttentionTransaction = typeof(IHasIndexedAttention).IsAssignableFrom(typeof(EntityType));
        _hasTaggableInterface = typeof(IShiftEntityTaggable).IsAssignableFrom(typeof(EntityType));

        if (this is IShiftEntityHasBeforeSaveHook<EntityType> beforeSaveHook)
            this.beforeSaveHook = beforeSaveHook;

        if (this is IShiftEntityHasAfterSaveHook<EntityType> afterSaveHook)
            this.afterSaveHook = afterSaveHook;

        if (shiftRepositoryBuilder is not null)
        {
            this.ShiftRepositoryOptions.SetCurrentUserProvider(this.currentUserProvider);

            // Optional resolution: these are only needed by the TypeAuth/claim/hash filters, so a
            // repository configured with options but without those services registered still constructs.
            this.ShiftRepositoryOptions.SetTypeAuthService(TryGetService<ITypeAuthService>(db)!);

            this.ShiftRepositoryOptions.SetHashIdService(TryGetService<IHashIdService>(db)!);

            shiftRepositoryBuilder.Invoke(this.ShiftRepositoryOptions);
        }
        // No builder: let the ENTITY configure this repository if it opts in via
        // IConfiguresShiftRepository<Entity, List, View> — includes, a small mapping tweak, etc. Keyed by the
        // endpoint's DTO triple, so an entity with several endpoints resolves the matching implementation.
        //
        // What matters is whether a builder was passed, NOT what class the repository is: passing one means "I
        // configure this myself" and takes over completely (the config twin of overriding without calling base);
        // passing none means "give me the default", so the entity's declaration stands — custom subclass or not.
        // The two are alternatives rather than layers because ShiftRepositoryOptions doesn't compose cleanly
        // (includes replace, filters accumulate, DataLevelAccess throws on a second declaration), so merging them
        // would be guesswork. The analyzer fails the build on the ambiguous case (entity declares config AND the
        // repository passes a builder) — SHENGEN006 — since this silently drops the entity's half.
        else if (typeof(IConfiguresShiftRepository<EntityType, ListDTO, ViewAndUpsertDTO>).IsAssignableFrom(typeof(EntityType)))
        {
            // Filters / data-level-access in the config need these providers; the null-builder path skipped
            // them, so set them before invoking the entity's configuration.
            this.ShiftRepositoryOptions.SetCurrentUserProvider(this.currentUserProvider);
            this.ShiftRepositoryOptions.SetTypeAuthService(TryGetService<ITypeAuthService>(db)!);
            this.ShiftRepositoryOptions.SetHashIdService(TryGetService<IHashIdService>(db)!);

            ((IConfiguresShiftRepository<EntityType, ListDTO, ViewAndUpsertDTO>)new EntityType())
                .ConfigureRepository(new ShiftRepositoryConfigurationContext<EntityType, ListDTO, ViewAndUpsertDTO>(
                    this.ShiftRepositoryOptions, MapperServiceProvider, this));
        }

        // Apply the default mapper only when the builder didn't call UseMapper(...) — so UseMapper(custom)
        // and UseMapper(null) both win over the default. Preference order:
        //   1. An IShiftEntityMapper<Entity, List, View> explicitly registered in DI (e.g. supplied through
        //      the [ShiftEntityEndpoint<…, TMapper>] attribute). Resolved via GetService (returns null when
        //      absent — no exception on the common no-mapper path).
        //   2. The registered AutoMapper, wrapped as an IShiftEntityMapper.
        //   3. Nothing — the mapping methods then throw "No mapper configured" unless overridden.
        // A ShiftRepository also implements IShiftEntityMapper<…>, so any repository resolution is ignored
        // to avoid a repository being used as its own (recursive) mapper.
        if (!this.ShiftRepositoryOptions.MapperConfigured)
        {
            var diMapper = MapperServiceProvider.GetService<IShiftEntityMapper<EntityType, ListDTO, ViewAndUpsertDTO>>();
            if (diMapper is not null && diMapper is not ShiftRepositoryBase)
            {
                this.ShiftRepositoryOptions.Mapper = diMapper;
            }
            else
            {
                var autoMapper = TryGetService<IMapper>(db);
                if (autoMapper is not null)
                    this.ShiftRepositoryOptions.Mapper = new AutoMapperShiftEntityMapper<EntityType, ListDTO, ViewAndUpsertDTO>(autoMapper);
            }
        }

        this.innerMapper = this.ShiftRepositoryOptions.Mapper;

        this.defaultDataLevelAccess = db.GetService<IDefaultDataLevelAccess>();
    }

    // The application service provider behind the DbContext (falling back to EF's internal provider on hosts
    // that didn't set one). Handed to the (virtual) mapping methods so a plugged IShiftEntityMapper can resolve
    // services on demand — letting a mapper stay unregistered (new-ed in UseMapper(...)) yet still reach DI.
    private IServiceProvider MapperServiceProvider
        => db.ApplicationServiceProvider ?? ((IInfrastructure<IServiceProvider>)db).Instance;

    #region Mapping Methods (public virtual — implements IShiftEntityMapper; default delegates to innerMapper)

    public virtual IQueryable<ListDTO> MapToList(IQueryable<EntityType> queryable, MappingContext context = default)
    {
        if (innerMapper is null)
            throw new InvalidOperationException(
                "No mapper configured. Override MapToList() or set one via UseMapper().");
        return innerMapper.MapToList(queryable, context.WithFallbackServices(MapperServiceProvider));
    }

    public virtual ViewAndUpsertDTO MapToView(EntityType entity, MappingContext context = default)
    {
        if (innerMapper is null)
            throw new InvalidOperationException(
                "No mapper configured. Override MapToView() or set one via UseMapper().");
        return innerMapper.MapToView(entity, context.WithFallbackServices(MapperServiceProvider));
    }

    public virtual EntityType MapToEntity(ViewAndUpsertDTO dto, EntityType existing, MappingContext context = default)
    {
        if (innerMapper is null)
            throw new InvalidOperationException(
                "No mapper configured. Override MapToEntity() or set one via UseMapper().");
        return innerMapper.MapToEntity(dto, existing, context.WithFallbackServices(MapperServiceProvider));
    }

    public virtual void CopyEntity(EntityType source, EntityType target, MappingContext context = default)
    {
        if (innerMapper is not null)
            innerMapper.CopyEntity(source, target, context.WithFallbackServices(MapperServiceProvider));
        else
            source.ShallowCopyTo(target);
    }

    #endregion

    public virtual async ValueTask<IQueryable<ListDTO>> OdataList(IQueryable<EntityType>? queryable)
    {
        if (queryable is null)
            queryable = await GetIQueryable(asOf: null, includes: null, disableDefaultDataLevelAccess: false, disableGlobalFilters: false);

        return MapToList(queryable, new MappingContext(MapperServiceProvider));
    }

    public virtual ValueTask<ViewAndUpsertDTO> ViewAsync(EntityType entity)
    {
        var dto = MapToView(entity, new MappingContext(MapperServiceProvider));

        // Read-side tagging is owned by the framework, not the entity mapper: when both the
        // entity and its DTO opt into tagging, populate the DTO's Tags from the entity's
        // (auto-included) Tags navigation. Mappers don't need to touch Tags at all.
        if (_hasTaggableInterface
            && entity is IShiftEntityTaggable taggableEntity
            && dto is IShiftEntityTaggableDTO taggableDto)
        {
            taggableDto.Tags = TagProjection.ToDtoList(taggableEntity.Tags);
        }

        return new ValueTask<ViewAndUpsertDTO>(dto);
    }

    /// <summary>
    /// The upsert entry point. Dispatches to the entity's <see cref="IUpsertsShiftRepository{EntityType, ListDTO, ViewAndUpsertDTO}"/>
    /// hook when the entity declares one for this triple, otherwise runs <see cref="DefaultUpsertAsync"/>.
    /// </summary>
    public virtual async ValueTask<EntityType> UpsertAsync(
        EntityType entity, ViewAndUpsertDTO dto,
        ActionTypes actionType,
        long? userId,
        Guid? idempotencyKey,
        bool disableDefaultDataLevelAccess,
        bool disableGlobalFilters
    )
    {
        // Entity-driven upsert: the entity can take this operation over by implementing
        // IUpsertsShiftRepository<Entity, List, View> — the analogue of overriding this method, with context.Base()
        // standing in for base.UpsertAsync(...). Keyed by the DTO triple, so an entity with several endpoints only
        // hooks the ones it declares.
        //
        // Deliberately NOT gated on _isBuiltInRepository: the hook is simply part of what this method does, so
        // ordinary override semantics decide whether a custom repository gets it — don't override => it runs;
        // override and call base.UpsertAsync(...) => it runs (nested, repository outermost); override without
        // calling base => it doesn't, because you replaced this method wholesale. Gating it would mean silently
        // discarding an entity's declared behavior the moment someone adds a repository class for an unrelated
        // reason (an extra Include, say) — a silent correctness loss with no error to notice.
        if (entity is IUpsertsShiftRepository<EntityType, ListDTO, ViewAndUpsertDTO> upsertHook)
        {
            // The hook receives this method's own parameters verbatim, plus a context carrying what an override
            // would have reached through `this` and `base` (the request scope, the repository, and the default).
            var context = new ShiftRepositoryUpsertContext<EntityType, ListDTO, ViewAndUpsertDTO>(
                MapperServiceProvider, this, entity, dto, actionType, userId, idempotencyKey,
                disableDefaultDataLevelAccess, disableGlobalFilters, DefaultUpsertAsync);

            return await upsertHook.UpsertAsync(entity, dto, actionType, userId, idempotencyKey,
                disableDefaultDataLevelAccess, disableGlobalFilters, context);
        }

        return await DefaultUpsertAsync(entity, dto, actionType, userId, idempotencyKey,
            disableDefaultDataLevelAccess, disableGlobalFilters);
    }

    /// <summary>
    /// The framework's default upsert — the body reached by <c>base.UpsertAsync(...)</c> from a custom repository
    /// and by <c>context.Base()</c> from an entity's upsert hook. Deliberately does NOT dispatch to the hook, so
    /// <c>Base()</c> cannot re-enter it and recurse forever.
    /// </summary>
    private async ValueTask<EntityType> DefaultUpsertAsync(
        EntityType entity, ViewAndUpsertDTO dto,
        ActionTypes actionType,
        long? userId,
        Guid? idempotencyKey,
        bool disableDefaultDataLevelAccess,
        bool disableGlobalFilters
    )
    {
        // Protected-row guard: an existing row marked IsProtected cannot be edited (checked before mapping, so the
        // incoming DTO can't overwrite the flag first). On Insert the entity is a fresh new() with IsProtected = false,
        // so this only blocks updates to protected/seeded rows — matching the per-repository guard it replaces. Opt in
        // by implementing IShiftEntityProtectable on the entity.
        if (entity is IShiftEntityProtectable { IsProtected: true })
            throw new ShiftEntityException(
                new Message("Forbidden", "This record is protected and cannot be modified or deleted."), (int)HttpStatusCode.Forbidden);

        entity = MapToEntity(dto, entity, new MappingContext(MapperServiceProvider, actionType));

        if (_hasTaggableInterface && dto is IShiftEntityTaggableDTO taggableDto && entity is IShiftEntityTaggable taggableEntity)
        {
            await TaggingPipeline.ApplyTagsAsync(this.db, taggableEntity, taggableDto.Tags);
        }

        var now = DateTimeOffset.UtcNow;

        // Source the acting user from the current user's claims when the caller didn't pass one — the same
        // identityClaimProvider the org/location claims below come from. SetAuditFields then stamps CreatedByUserID
        // (insert) and LastSavedByUserID from it, skipping any value already set on the entity.
        userId ??= identityClaimProvider.GetUserID();

        this.SetAuditFields(entity, actionType == ActionTypes.Insert, userId, now);

        if (idempotencyKey != null)
        {
            (entity as IEntityHasIdempotencyKey<EntityType>)!.IdempotencyKey = idempotencyKey;
        }

        if (actionType == ActionTypes.Insert)
        {
            SetCreationClaimDefaults(entity);
        }

        if (!disableDefaultDataLevelAccess)
        {
            // Insert/Edit ⇒ the Write level (D6). The check runs against the MAPPED entity (and, on Insert, after
            // the claim-default backfill above), so what gets authorized is exactly what would be saved.
            var canWrite = HasDataLevelAccess(entity, TypeAuth.Core.Access.Write);

            if (!canWrite)
            {
                var messageText = actionType == ActionTypes.Insert ? "Can Not Create Item" : "Can Not Update Item";

                throw new ShiftEntityException(new Message("Forbidden", messageText), (int)HttpStatusCode.Forbidden);
            }
        }

        return entity;
    }

    /// <summary>
    /// <see cref="UpsertAsync(EntityType, ViewAndUpsertDTO, ActionTypes, long?, Guid?, bool, bool)"/> with the named
    /// <see cref="RepositoryBypass"/> vocabulary instead of the positional bool pair. Deliberately
    /// <b>non-virtual</b>: it forwards into the bool-taking virtual, so repositories that override it keep
    /// receiving every call regardless of which overload the caller used (override the bool overload to customize).
    /// </summary>
    public ValueTask<EntityType> UpsertAsync(
        EntityType entity,
        ViewAndUpsertDTO dto,
        ActionTypes actionType,
        long? userId,
        Guid? idempotencyKey = null,
        RepositoryBypass bypass = RepositoryBypass.None)
        => UpsertAsync(entity, dto, actionType, userId, idempotencyKey,
            disableDefaultDataLevelAccess: bypass.HasFlag(RepositoryBypass.DataLevelAccess),
            disableGlobalFilters: bypass.HasFlag(RepositoryBypass.GlobalFilters));

    public Message? ResponseMessage { get; set; }
    public Dictionary<string, object>? AdditionalResponseData { get; set; }

    private async Task<EntityType?> BaseFindAsync(long id, DateTimeOffset? asOf, Guid? idempotencyKey, bool disableDefaultDataLevelAccess, bool disableGlobalFilters)
    {
        List<string>? includes = null;

        if (this.ShiftRepositoryOptions is not null)
        {
            includes = new();

            foreach (var i in this.ShiftRepositoryOptions.IncludeOperations)
            {
                IncludeOperations<EntityType> operation = new();
                i.Invoke(operation);
                includes.Add(operation.Includes);
            }
        }

        // Auto-include the Tags navigation for taggable entities so single-entity reads
        // (GET/{id}, and POST/PUT response bodies built from ViewAsync) always carry tags.
        // Programmers no longer hand-add .Include(x => x.Tags) per repository.
        if (_hasTaggableInterface)
        {
            includes ??= new();
            if (!includes.Contains(nameof(IShiftEntityTaggable.Tags)))
                includes.Add(nameof(IShiftEntityTaggable.Tags));
        }

        // WhenDenied(Forbidden) (D7, 3.3): to refuse an out-of-scope row loudly the repository must SEE it — skip the
        // v2 query filter for this single-row fetch and let the row check below produce the 403. That is what
        // distinguishes "exists but out of scope" (403) from "doesn't exist" (null ⇒ the caller's 404). Under
        // NotFound (the default) the fetch stays filtered and an out-of-scope row is simply invisible. Only this
        // fetch changes: lists always filter (GetIQueryable callers are untouched), and the caller's
        // disableDefaultDataLevelAccess bypass keeps skipping fetch-filter and row check alike.
        var revealOutOfScopeRow =
            this.ShiftRepositoryOptions?.DataLevelAccessPolicy?.DeniedBehavior == DataLevelDeniedBehavior.Forbidden;

        var q = await GetIQueryable(asOf, includes, disableDefaultDataLevelAccess || revealOutOfScopeRow, disableGlobalFilters);

        EntityType? entity = null;

        if (id != 0)
        {
            entity = await q.FirstOrDefaultAsync(x =>
                EF.Property<long>(x, nameof(ShiftEntity<EntityType>.ID)) == id
            );
        }
        else if (idempotencyKey != null)
        {
            entity = await q.FirstOrDefaultAsync(x =>
                EF.Property<Guid?>(x, nameof(IEntityHasIdempotencyKey<EntityType>.IdempotencyKey)) == idempotencyKey
            );
        }

        if (entity is not null && includes?.Count > 0)
            entity.ReloadAfterSave = true;

        if (!disableDefaultDataLevelAccess)
        {
            // Find is a View ⇒ the Read level (D6). With a v2 policy the query above is already Read-filtered (3.1),
            // so an out-of-scope id comes back null — and a null entity passes (nothing to authorize; see
            // HasDataLevelAccess). Under WhenDenied(Forbidden) the fetch was deliberately unfiltered instead, and
            // this check is what denies an existing out-of-scope row (403 "Can Not Read Item"). The legacy path
            // keeps row-checking even the null entity, exactly as today.
            var canRead = HasDataLevelAccess(entity, TypeAuth.Core.Access.Read);

            if (!canRead)
                throw new ShiftEntityException(new Message("Forbidden", "Can Not Read Item"), (int)HttpStatusCode.Forbidden);
        }

        return entity;
    }

    public virtual async Task<EntityType?> FindAsync(long id, DateTimeOffset? asOf, bool disableDefaultDataLevelAccess, bool disableGlobalFilters)
    {
        return await BaseFindAsync(id, asOf, null, disableDefaultDataLevelAccess, disableGlobalFilters);
    }

    /// <summary>
    /// <see cref="FindAsync(long, DateTimeOffset?, bool, bool)"/> with the named <see cref="RepositoryBypass"/>
    /// vocabulary instead of the positional bool pair. Deliberately <b>non-virtual</b>: it forwards into the
    /// bool-taking virtual, so repositories that override it keep receiving every call regardless of which
    /// overload the caller used (override the bool overload to customize).
    /// </summary>
    public Task<EntityType?> FindAsync(long id, DateTimeOffset? asOf = null, RepositoryBypass bypass = RepositoryBypass.None)
        => FindAsync(id, asOf,
            disableDefaultDataLevelAccess: bypass.HasFlag(RepositoryBypass.DataLevelAccess),
            disableGlobalFilters: bypass.HasFlag(RepositoryBypass.GlobalFilters));

    public virtual async Task<EntityType?> FindByIdempotencyKeyAsync(Guid idempotencyKey, DateTimeOffset? asOf,bool disableDefaultDataLevelAccess, bool disableGlobalFilters)
    {
        return await BaseFindAsync(0, asOf: asOf, idempotencyKey: idempotencyKey, disableDefaultDataLevelAccess: disableDefaultDataLevelAccess, disableGlobalFilters: disableGlobalFilters);
    }

    /// <summary>
    /// <see cref="FindByIdempotencyKeyAsync(Guid, DateTimeOffset?, bool, bool)"/> with the named
    /// <see cref="RepositoryBypass"/> vocabulary instead of the positional bool pair. Deliberately
    /// <b>non-virtual</b>: it forwards into the bool-taking virtual, so repositories that override it keep
    /// receiving every call regardless of which overload the caller used (override the bool overload to customize).
    /// </summary>
    public Task<EntityType?> FindByIdempotencyKeyAsync(Guid idempotencyKey, DateTimeOffset? asOf = null, RepositoryBypass bypass = RepositoryBypass.None)
        => FindByIdempotencyKeyAsync(idempotencyKey, asOf,
            disableDefaultDataLevelAccess: bypass.HasFlag(RepositoryBypass.DataLevelAccess),
            disableGlobalFilters: bypass.HasFlag(RepositoryBypass.GlobalFilters));

    public virtual async ValueTask<IQueryable<EntityType>> GetIQueryable(DateTimeOffset? asOf, List<string>? includes, bool disableDefaultDataLevelAccess, bool disableGlobalFilters)
    {
        var query = asOf is null ? dbSet.AsQueryable() : dbSet.TemporalAsOf(asOf.Value.UtcDateTime);

        if (includes is not null)
        {
            foreach (var include in includes)
                query = query.Include(include);
        }

        if (!disableDefaultDataLevelAccess)
            query = ApplyDataLevelFilters(query);

        if (!disableGlobalFilters)
            query = await query.ApplyGlobalRepositoryFiltersAsync(this.ShiftRepositoryOptions.GlobalRepositoryFilters);

        return query;
    }

    /// <summary>
    /// <see cref="GetIQueryable(DateTimeOffset?, List{string}?, bool, bool)"/> with the named
    /// <see cref="RepositoryBypass"/> vocabulary instead of the positional bool pair. Deliberately
    /// <b>non-virtual</b>: it forwards into the bool-taking virtual, so repositories that override it keep
    /// receiving every call regardless of which overload the caller used (override the bool overload to customize).
    /// </summary>
    public ValueTask<IQueryable<EntityType>> GetIQueryable(
        DateTimeOffset? asOf = null, List<string>? includes = null, RepositoryBypass bypass = RepositoryBypass.None)
        => GetIQueryable(asOf, includes,
            disableDefaultDataLevelAccess: bypass.HasFlag(RepositoryBypass.DataLevelAccess),
            disableGlobalFilters: bypass.HasFlag(RepositoryBypass.GlobalFilters));

    /// <summary>
    /// The query-path data-level filter: when a v2 policy was declared via
    /// <see cref="ShiftRepositoryOptions{EntityType, ListDTO, ViewAndUpsertDTO}.DataLevelAccess"/> the declaration is the whole truth for the
    /// entity — its filter replaces the legacy default filters entirely (an explicit <c>Unscoped()</c> applies no
    /// filter from either path). With no declaration, today's legacy default filters run unchanged (opt-in
    /// coexistence, D1).
    /// </summary>
    private IQueryable<EntityType> ApplyDataLevelFilters(IQueryable<EntityType> query)
    {
        var policy = this.ShiftRepositoryOptions.DataLevelAccessPolicy;

        if (policy is null)
            return this.defaultDataLevelAccess!.ApplyDefaultDataLevelFilters(this.ShiftRepositoryOptions.DefaultDataLevelAccessOptions, query);

        // An explicit opt-out needs no filtering and no per-request context — short-circuit before resolving it,
        // so an unscoped entity also works on hosts that never registered data-level access.
        if (policy.IsUnscoped)
            return query;

        // Querying is a View ⇒ the Read level (D6); Insert/Edit/Delete pick their own levels on the row paths (3.2).
        return policy.ApplyQueryFilter(query, TypeAuth.Core.Access.Read, GetRequiredDataLevelAccessContext());
    }

    /// <summary>
    /// The row-path data-level check — the row twin of <see cref="ApplyDataLevelFilters"/>, with the same routing:
    /// a declared v2 policy means the verdict is <see cref="DataLevelAccessPolicy{TEntity}.Authorize"/> and the
    /// legacy row check does <b>not</b> also run (the declaration is the whole truth for the entity); no declaration
    /// means today's legacy <see cref="IDefaultDataLevelAccess.HasDefaultDataLevelAccess"/> runs unchanged (opt-in
    /// coexistence, D1). The operation site picks the level (D6): Find/View ⇒ Read, Insert/Edit ⇒ Write,
    /// Delete ⇒ Delete — so a Read-only grant can View a row but never write or delete it.
    /// <para>
    /// An explicit <c>Unscoped()</c> passes without resolving the per-request context, so an unscoped entity works
    /// on hosts that never registered data-level access. A null entity (a Find that came back empty — with a policy
    /// declared the query path already filtered it out, 3.1) has nothing to authorize and passes — the caller's null
    /// surfaces as 404. The entity's <see cref="DataLevelDeniedBehavior"/> declaration (<c>WhenDenied</c>, D7) can
    /// flip that disclosure: under <c>Forbidden</c>, <c>BaseFindAsync</c> fetches unfiltered and this check is what
    /// denies an existing out-of-scope row with 403. The legacy path keeps receiving the null entity exactly as today.
    /// </para>
    /// </summary>
    private bool HasDataLevelAccess(EntityType? entity, Access access)
    {
        var policy = this.ShiftRepositoryOptions.DataLevelAccessPolicy;

        if (policy is null)
            return this.defaultDataLevelAccess!.HasDefaultDataLevelAccess(
                this.ShiftRepositoryOptions.DefaultDataLevelAccessOptions, entity, access);

        if (policy.IsUnscoped)
            return true;

        if (entity is null)
            return true;

        return policy.Authorize(entity, access, GetRequiredDataLevelAccessContext());
    }

    /// <summary>
    /// The per-request v2 <see cref="DataLevelAccessContext"/>, resolved lazily so repositories without a declared
    /// policy never depend on it (non-web hosts may not register data-level access at all). A declared policy with
    /// no resolvable context is fatal — fail closed; running the query unfiltered would leak out-of-scope rows.
    /// </summary>
    private DataLevelAccessContext GetRequiredDataLevelAccessContext()
    {
        var serviceProvider = db.ApplicationServiceProvider ?? ((IInfrastructure<IServiceProvider>)db).Instance;

        return serviceProvider.GetService<DataLevelAccessContext>()
            ?? throw new InvalidOperationException(
                $"'{typeof(EntityType).Name}' declares data-level access, but no {nameof(DataLevelAccessContext)} is " +
                $"registered. Call AddShiftEntityDataLevelAccess() on the host's service collection " +
                $"(AddShiftEntityWebSharedCore does this automatically).");
    }

    public virtual IQueryable<RevisionDTO> GetRevisionsAsync(long id)
    {
        return dbSet
                .TemporalAll()
                .AsNoTracking()
                .Where(x => EF.Property<long>(x, nameof(ShiftEntity<EntityType>.ID)) == id)
                .Select(x => new
                {
                    ID = EF.Property<long>(x, nameof(ShiftEntity<EntityType>.ID)),
                    ValidFrom = EF.Property<DateTime>(x, "PeriodStart"),
                    ValidTo = EF.Property<DateTime>(x, "PeriodEnd"),
                    SavedByUserID = EF.Property<long?>(x, nameof(ShiftEntity<EntityType>.LastSavedByUserID)),
                })
                .Select(x => new RevisionDTO
                {
                    ID = x.ID.ToString(),
                    ValidFrom = x.ValidFrom,
                    ValidTo = x.ValidTo,
                    SavedByUserID = x.SavedByUserID == null ? null : x.SavedByUserID.ToString(),
                });
    }

    public virtual void Add(EntityType entity)
    {
        if (this.ShiftRepositoryOptions is not null && this.ShiftRepositoryOptions.IncludeOperations.Count > 0)
        {
            entity.ReloadAfterSave = true;
        }

        dbSet.Add(entity);
    }

    // Audit stamping rules live in AuditStamper, shared with ShiftDbContext's SaveChanges override so both paths
    // behave identically. The repository can resolve its claim values eagerly — identityClaimProvider is always
    // registered here (the DbContext fallback resolves them defensively instead).
    private void SetAuditFields(IShiftEntityAudit entity, bool isAdded, long? userId, DateTimeOffset now)
        => AuditStamper.StampAuditFields(entity, isAdded, userId, now);

    private void SetCreationClaimDefaults(object entity)
        => AuditStamper.StampCreationClaims(
            entity,
            identityClaimProvider.GetCountryID(),
            identityClaimProvider.GetRegionID(),
            identityClaimProvider.GetCityID(),
            identityClaimProvider.GetCompanyID(),
            identityClaimProvider.GetCompanyBranchID());

    // Runs every DI-registered IShiftEntitySaveValidator against the pending unit of work before the repository
    // persists it — the seam that lets cross-cutting, save-time rules (e.g. ShiftIdentity's feature locking) live
    // outside any repository. Only repository saves reach here; direct DbContext saves (seeding/replication) do not.
    // Resolution is optional: hosts that register no validator (or provide no application service provider) skip it.
    private void RunSaveValidators()
    {
        // Resolve from the application (request-scoped) service provider — the same one the repository uses for its
        // other on-demand services. GetServices never returns null (empty when none are registered), and resolves
        // scoped validators correctly. EF's db.GetService<IEnumerable<T>>() does not bridge IEnumerable to the app
        // provider, so it can't be used here.
        var validators = MapperServiceProvider.GetServices<IShiftEntitySaveValidator>().ToList();
        if (validators.Count == 0)
            return;

        List<EntityEntry>? pendingWrites = null;
        foreach (var entry in db.ChangeTracker.Entries())
        {
            if (entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                (pendingWrites ??= new()).Add(entry);
        }

        if (pendingWrites is null)
            return;

        foreach (var validator in validators)
            validator.Validate(pendingWrites);
    }

    public virtual async Task<int> SaveChangesAsync()
    {
        RunSaveValidators();

        var now = DateTimeOffset.UtcNow;
        var hashIdService = this.db.GetService<IHashIdService>();
        long? userId = this.currentUserProvider?.GetUser()?.GetUserID(hashIdService);
        var beforeSaveTasks = new List<ValueTask>();
        var afterSaveEntities = new List<(EntityType entity, ActionTypes action)>();

        if (this.afterSaveHook is not null || _needsAttentionTransaction)
        {
            return await SaveChangesWithTransactionAsync(now, userId, beforeSaveTasks, afterSaveEntities);
        }
        else
        {
            return await SaveChangesWithoutTransactionAsync(now, userId, beforeSaveTasks, afterSaveEntities);
        }
    }

    private async Task<int> SaveChangesWithTransactionAsync(
        DateTimeOffset now,
        long? userId,
        List<ValueTask> beforeSaveTasks,
        List<(EntityType entity, ActionTypes action)> afterSaveEntities)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();

        int result;
        List<AttentionRaised>? raisedAttention;

        try
        {
            (result, raisedAttention) = await ProcessEntriesAndSave(now, userId, beforeSaveTasks, afterSaveEntities);

            // Execute all AfterSave hooks - if this fails, transaction will rollback
            var afterSaveTasks = afterSaveEntities.Select(x => this.afterSaveHook!.AfterSaveAsync(x.entity, x.action));
            await Task.WhenAll(afterSaveTasks.Select(vt => vt.AsTask()));

            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }

        // Only after a successful commit — consumers must never observe a rolled-back signal
        await PublishAttentionRaisedAsync(raisedAttention);

        return result;
    }

    private async Task<int> SaveChangesWithoutTransactionAsync(
        DateTimeOffset now,
        long? userId,
        List<ValueTask> beforeSaveTasks,
        List<(EntityType entity, ActionTypes action)> afterSaveEntities)
    {
        var (result, raisedAttention) = await ProcessEntriesAndSave(now, userId, beforeSaveTasks, afterSaveEntities);

        // No AfterSave to execute since it's not overridden

        await PublishAttentionRaisedAsync(raisedAttention);

        return result;
    }

    /// <summary>
    /// Publishes one <see cref="AttentionRaised"/> event per newly-raised signal through the
    /// registered <see cref="IAttentionDispatcher"/>. No-op when nothing was raised or when
    /// no dispatcher is registered (emission is opt-in via <c>AddAttentionEmission()</c> /
    /// <c>AddAttentionConsumer&lt;T&gt;()</c>). Must only be called after the save has
    /// committed.
    /// </summary>
    private async Task PublishAttentionRaisedAsync(List<AttentionRaised>? raisedAttention)
    {
        if (raisedAttention is null || raisedAttention.Count == 0)
            return;

        var serviceProvider = db.ApplicationServiceProvider ?? ((IInfrastructure<IServiceProvider>)db).Instance;
        var dispatcher = serviceProvider.GetService<IAttentionDispatcher>();

        if (dispatcher is null)
            return;

        foreach (var attentionRaised in raisedAttention)
            await dispatcher.PublishAsync(attentionRaised);
    }

    private async Task<(int result, List<AttentionRaised>? raisedAttention)> ProcessEntriesAndSave(
        DateTimeOffset now,
        long? userId,
        List<ValueTask> beforeSaveTasks,
        List<(EntityType entity, ActionTypes action)> afterSaveEntities)
    {
        var entitiesToReload = new List<EntityType>();
        List<PendingIndexedSignal>? allPendingSignals = null;
        List<AttentionEntityOutcome>? attentionOutcomes = null;

        foreach (var entry in db.ChangeTracker.Entries())
        {
            var added = entry.State == EntityState.Added;
            var modified = entry.State == EntityState.Modified;

            if (!added && !modified)
                continue;

            // Audit columns are stamped on EVERY changed auditable row in the unit of work — not just this
            // repository's own entity type. A single SaveChanges flushes the whole ChangeTracker, so cascaded
            // children and unrelated entities (any ShiftEntity<T>) must be stamped here too. A row already handled
            // upstream (UpsertAsync sets the guard) is left alone; the rest get the same backfill upsert would do —
            // dates/user via SetAuditFields, plus the insert-only org/location claims.
            if (entry.Entity is IShiftEntityAudit auditable)
            {
                var alreadyStamped = auditable.AuditFieldsAreSet;
                this.SetAuditFields(auditable, added, userId, now);

                if (added && !alreadyStamped)
                    this.SetCreationClaimDefaults(entry.Entity);
            }

            // The remaining work below is specific to this repository's own entity type.
            if (entry.Entity is not EntityType entityType)
                continue;

            // Only process unique hash if the interface is implemented
            if (_hasUniqueHashInterface && entityType is IEntityHasUniqueHash<EntityType> entryWithUniqueHash)
            {
                var uniqueHash = entryWithUniqueHash.CalculateUniqueHash();
                if (uniqueHash is not null)
                {
                    using var sha256 = System.Security.Cryptography.SHA256.Create();
                    var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(uniqueHash));
                    entry.Property("UniqueHash").CurrentValue = hashBytes;
                }
            }

            var actionType = added ? ActionTypes.Insert : ActionTypes.Update;

            // Only call BeforeSave if it's overridden
            if (this.beforeSaveHook is not null)
            {
                beforeSaveTasks.Add(this.beforeSaveHook.BeforeSaveAsync(entityType, actionType));
            }

            // Only track entities for AfterSave if it's overridden
            if (this.afterSaveHook is not null)
            {
                afterSaveEntities.Add((entityType, actionType));
            }

            // Attention evaluation
            if (_hasAttentionInterface)
            {
                var original = added ? null : (EntityType?)entry.OriginalValues.ToObject();
                var serviceProvider = db.ApplicationServiceProvider ?? ((IInfrastructure<IServiceProvider>)db).Instance;

                var outcome = await AttentionPipeline.ProcessEntity(
                    db, entry, entityType, original, actionType, serviceProvider);

                if (outcome is not null)
                {
                    attentionOutcomes ??= [];
                    attentionOutcomes.Add(outcome);

                    if (outcome.PendingIndexed is not null)
                    {
                        allPendingSignals ??= [];
                        allPendingSignals.AddRange(outcome.PendingIndexed);
                    }
                }
            }

            // Track entities that need reload after save
            if (entityType.ReloadAfterSave)
            {
                entitiesToReload.Add(entityType);
            }
        }

        // Execute all BeforeSave hooks only if there are any
        if (beforeSaveTasks.Count > 0)
        {
            await Task.WhenAll(beforeSaveTasks.Select(vt => vt.AsTask()));
        }

        // The sweep above already stamped every tracked auditable row, so suppress the context's SaveChanges override
        // for these repository-initiated saves — otherwise it would run its (insert-only) backfill a second time.
        // (The AuditFieldsAreSet guard already prevents double-writing; this also avoids the redundant second pass.)
        int result;
        using (db.SuppressAuditStamping())
        {
            try
            {
                result = await db.SaveChangesAsync();
            }
            catch (DbUpdateException dbUpdateException)
            {
                if (dbUpdateException.InnerException is SqlException sqlException)
                {
                    var error = sqlException.Errors
                        .OfType<SqlError>()
                        .FirstOrDefault(se => se.Number == 2601);

                    if (error?.Message?.Contains(nameof(IEntityHasIdempotencyKey<EntityType>.IdempotencyKey)) ?? false)
                        throw new DuplicateIdempotencyKeyException(error.Message);

                    if (error?.Message?.Contains(nameof(IEntityHasUniqueHash.UniqueHash)) ?? false)
                        throw new ShiftEntityException(
                            new Message("Conflict", "An item with the same Unique Fields already exists."),
                            (int)HttpStatusCode.Conflict
                        );
                }

                throw;
            }

            // Flush pending indexed attention signals (INSERT case — entity IDs now available)
            if (allPendingSignals is { Count: > 0 })
            {
                AttentionPipeline.FlushPendingSignals(db, allPendingSignals);
                await db.SaveChangesAsync();
            }
        }

        // Materialize AttentionRaised events now that every entity has its database ID.
        // The caller publishes them only after the save/transaction fully succeeds.
        List<AttentionRaised>? raisedAttention = null;

        if (attentionOutcomes is not null)
        {
            raisedAttention = [];

            // The originating window's hub connection id (stamped on the request via
            // AttentionRealtime.OriginHeader), captured now while the request context is current —
            // the real-time fan-out runs later on a background loop where it can no longer be
            // read. Carried on each event so the notifier can exclude that window. Null when the
            // hub isn't wired or the save has no originating hub connection (background/timer).
            var attentionServiceProvider = db.ApplicationServiceProvider ?? ((IInfrastructure<IServiceProvider>)db).Instance;
            var originConnectionId = attentionServiceProvider.GetService<IAttentionOriginProvider>()?.OriginConnectionId;

            foreach (var outcome in attentionOutcomes)
            {
                var attentionEntityId = (long)outcome.Entry.Property("ID").CurrentValue!;

                foreach (var signal in outcome.NewSignals)
                {
                    raisedAttention.Add(new AttentionRaised
                    {
                        EntityType = outcome.EntityTypeName,
                        EntityId = attentionEntityId,
                        Signal = signal,
                        OriginConnectionId = originConnectionId,
                    });
                }
            }
        }

        // Reload entities that have navigation properties (Includes)
        if (entitiesToReload.Count > 0)
        {
            foreach (var trackedEntity in entitiesToReload)
            {
                var freshEntity = await FindAsync(trackedEntity.ID, bypass: RepositoryBypass.All);
                if (freshEntity is not null)
                    // Refresh the tracked entity from the fresh DB load via the mapper's CopyEntity (a top-level
                    // copy: scalars + navigation references, real keys preserved).
                    CopyEntity(freshEntity, trackedEntity, new MappingContext(MapperServiceProvider));
            }
        }

        return (result, raisedAttention);
    }

    /// <summary>
    /// The delete entry point. Dispatches to the entity's <see cref="IDeletesShiftRepository{EntityType, ListDTO, ViewAndUpsertDTO}"/>
    /// hook when the entity declares one for this triple, otherwise runs <see cref="DefaultDeleteAsync"/>.
    /// </summary>
    public virtual async ValueTask<EntityType> DeleteAsync(EntityType entity, long? userId, bool disableDefaultDataLevelAccess, bool disableGlobalFilters)
    {
        // Entity-driven delete — the twin of the upsert dispatch above; see its comment for the rules.
        if (entity is IDeletesShiftRepository<EntityType, ListDTO, ViewAndUpsertDTO> deleteHook)
        {
            var context = new ShiftRepositoryDeleteContext<EntityType, ListDTO, ViewAndUpsertDTO>(
                MapperServiceProvider, this, entity, userId,
                disableDefaultDataLevelAccess, disableGlobalFilters, DefaultDeleteAsync);

            return await deleteHook.DeleteAsync(entity, userId,
                disableDefaultDataLevelAccess, disableGlobalFilters, context);
        }

        return await DefaultDeleteAsync(entity, userId, disableDefaultDataLevelAccess, disableGlobalFilters);
    }

    /// <summary>
    /// The framework's default delete — the body reached by <c>base.DeleteAsync(...)</c> from a custom repository
    /// and by <c>context.Base()</c> from an entity's delete hook. Deliberately does NOT dispatch to the hook, so
    /// <c>Base()</c> cannot re-enter it and recurse forever.
    /// </summary>
    private ValueTask<EntityType> DefaultDeleteAsync(EntityType entity, long? userId, bool disableDefaultDataLevelAccess, bool disableGlobalFilters)
    {
        // Protected-row guard (see UpsertAsync): a protected row can't be deleted. Checked before the data-level check
        // so a protected row is reported as protected rather than as an access denial — matching the per-repository
        // guard it replaces.
        if (entity is IShiftEntityProtectable { IsProtected: true })
            throw new ShiftEntityException(
                new Message("Forbidden", "This record is protected and cannot be modified or deleted."), (int)HttpStatusCode.Forbidden);

        if (!disableDefaultDataLevelAccess)
        {
            // Delete ⇒ the Delete level (D6); denial happens before the soft-delete flag is touched.
            var canDelete = HasDataLevelAccess(entity, TypeAuth.Core.Access.Delete);

            if (!canDelete)
                throw new ShiftEntityException(new Message("Forbidden", "Can Not Delete Item"), (int)HttpStatusCode.Forbidden);
        }

        entity.IsDeleted = true;

        // Record the deleter from the current user's claims when not passed explicitly. There is no dedicated
        // DeletedByUserID column, so the deleter is captured in LastSavedByUserID by the stamp below.
        userId ??= identityClaimProvider.GetUserID();

        this.SetAuditFields(entity, false, userId, DateTimeOffset.UtcNow);

        return new ValueTask<EntityType>(entity);
    }

    /// <summary>
    /// <see cref="DeleteAsync(EntityType, long?, bool, bool)"/> with the named <see cref="RepositoryBypass"/>
    /// vocabulary instead of the positional bool pair. Deliberately <b>non-virtual</b>: it forwards into the
    /// bool-taking virtual, so repositories that override it keep receiving every call regardless of which
    /// overload the caller used (override the bool overload to customize).
    /// </summary>
    public ValueTask<EntityType> DeleteAsync(
        EntityType entity, long? userId, RepositoryBypass bypass = RepositoryBypass.None)
        => DeleteAsync(entity, userId,
            disableDefaultDataLevelAccess: bypass.HasFlag(RepositoryBypass.DataLevelAccess),
            disableGlobalFilters: bypass.HasFlag(RepositoryBypass.GlobalFilters));

    public virtual Task<Stream> PrintAsync(string id)
    {
        throw new NotImplementedException();
    }

    public virtual ValueTask<IQueryable<ListDTO>> ApplyPostODataProcessing(IQueryable<ListDTO> queryable)
    {
        return new ValueTask<IQueryable<ListDTO>>(queryable);
    }
}
