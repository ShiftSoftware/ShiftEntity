using AutoMapper;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Attention;
using ShiftSoftware.ShiftEntity.Core.DataLevelAccess;
using ShiftSoftware.ShiftEntity.Core.Flags;
using ShiftSoftware.ShiftEntity.EFCore.Attention;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.ShiftEntity.Model.Flags;
using ShiftSoftware.TypeAuth.Core;
using System.Net;
using System.Security.Claims;
using System.Text;

namespace ShiftSoftware.ShiftEntity.EFCore;

public class ShiftRepository<DB, EntityType, ListDTO, ViewAndUpsertDTO> :
    ShiftRepositoryBase,
    IShiftRepositoryAsync<EntityType, ListDTO, ViewAndUpsertDTO>,
    IShiftRepositoryWithOptions<EntityType>
    where DB : ShiftDbContext
    where EntityType : ShiftEntity<EntityType>, new()
    where ListDTO : ShiftEntityDTOBase
{
    public DB db { get; private set; } = default!;
    internal DbSet<EntityType> dbSet = default!;
    public override object? GetDbContext() => db;

    [Obsolete("Use entityMapper instead. This property is kept for backwards compatibility.")]
    public IMapper? mapper => (entityMapper as AutoMapperShiftEntityMapper<EntityType, ListDTO, ViewAndUpsertDTO>)?.Mapper;

    protected IShiftEntityMapper<EntityType, ListDTO, ViewAndUpsertDTO>? entityMapper { get; private set; }
    public ShiftRepositoryOptions<EntityType> ShiftRepositoryOptions { get; set; } = default!;
    public IDefaultDataLevelAccess? defaultDataLevelAccess { get; private set; }
    public IdentityClaimProvider identityClaimProvider { get; private set; } = default!;
    public ICurrentUserProvider? currentUserProvider { get; private set; }
    private bool _hasUniqueHashInterface;
    private bool _hasAttentionInterface;
    private bool _needsAttentionTransaction;

    private IShiftEntityHasBeforeSaveHook<EntityType>? beforeSaveHook = null;
    private IShiftEntityHasAfterSaveHook<EntityType>? afterSaveHook = null;

    public ShiftRepository(DB db, Action<ShiftRepositoryOptions<EntityType>>? shiftRepositoryBuilder = null)
    {
        var autoMapper = db.GetService<IMapper>();
        if (autoMapper is not null)
            this.entityMapper = new AutoMapperShiftEntityMapper<EntityType, ListDTO, ViewAndUpsertDTO>(autoMapper);
        InitCommon(db, shiftRepositoryBuilder);
    }

    public ShiftRepository(DB db,
        IShiftEntityMapper<EntityType, ListDTO, ViewAndUpsertDTO> entityMapper,
        Action<ShiftRepositoryOptions<EntityType>>? shiftRepositoryBuilder = null)
    {
        this.entityMapper = entityMapper;
        InitCommon(db, shiftRepositoryBuilder);
    }

    private void InitCommon(DB db, Action<ShiftRepositoryOptions<EntityType>>? shiftRepositoryBuilder)
    {
        this.db = db;
        this.dbSet = db.Set<EntityType>();
        this.identityClaimProvider = db.GetService<IdentityClaimProvider>();
        this.currentUserProvider = db.GetService<ICurrentUserProvider>();
        this.ShiftRepositoryOptions = new();

        _hasUniqueHashInterface = typeof(EntityType).GetInterfaces()
            .Any(x => x.IsAssignableFrom(typeof(IEntityHasUniqueHash<EntityType>)));

        _hasAttentionInterface = typeof(IHasAttention).IsAssignableFrom(typeof(EntityType));
        _needsAttentionTransaction = typeof(IHasIndexedAttention).IsAssignableFrom(typeof(EntityType));

        if (this is IShiftEntityHasBeforeSaveHook<EntityType> beforeSaveHook)
            this.beforeSaveHook = beforeSaveHook;

        if (this is IShiftEntityHasAfterSaveHook<EntityType> afterSaveHook)
            this.afterSaveHook = afterSaveHook;

        if (shiftRepositoryBuilder is not null)
        {
            this.ShiftRepositoryOptions.SetCurrentUserProvider(this.currentUserProvider);

            this.ShiftRepositoryOptions.SetTypeAuthService(db.GetService<ITypeAuthService>());

            this.ShiftRepositoryOptions.SetHashIdService(db.GetService<IHashIdService>());

            shiftRepositoryBuilder.Invoke(this.ShiftRepositoryOptions);
        }

        this.defaultDataLevelAccess = db.GetService<IDefaultDataLevelAccess>();
    }

    #region Mapping Methods (virtual, delegates to entityMapper)

    protected virtual IQueryable<ListDTO> MapToList(IQueryable<EntityType> queryable)
    {
        if (entityMapper is null)
            throw new InvalidOperationException(
                "No mapper configured. Override MapToList() or provide an IShiftEntityMapper.");
        return entityMapper.MapToList(queryable);
    }

    protected virtual ViewAndUpsertDTO MapToView(EntityType entity)
    {
        if (entityMapper is null)
            throw new InvalidOperationException(
                "No mapper configured. Override MapToView() or provide an IShiftEntityMapper.");
        return entityMapper.MapToView(entity);
    }

    protected virtual EntityType MapToEntity(ViewAndUpsertDTO dto, EntityType existing)
    {
        if (entityMapper is null)
            throw new InvalidOperationException(
                "No mapper configured. Override MapToEntity() or provide an IShiftEntityMapper.");
        return entityMapper.MapToEntity(dto, existing);
    }

    #endregion

    public virtual async ValueTask<IQueryable<ListDTO>> OdataList(IQueryable<EntityType>? queryable)
    {
        if (queryable is null)
            queryable = await GetIQueryable(asOf: null, includes: null, disableDefaultDataLevelAccess: false, disableGlobalFilters: false);

        return MapToList(queryable);
    }

    public virtual ValueTask<ViewAndUpsertDTO> ViewAsync(EntityType entity)
    {
        return new ValueTask<ViewAndUpsertDTO>(MapToView(entity));
    }

    public virtual ValueTask<EntityType> UpsertAsync(
        EntityType entity, ViewAndUpsertDTO dto,
        ActionTypes actionType,
        long? userId,
        Guid? idempotencyKey,
        bool disableDefaultDataLevelAccess,
        bool disableGlobalFilters
    )
    {
        entity = MapToEntity(dto, entity);

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
            if (entity is IEntityHasCountry<EntityType> entityWithCountry && entityWithCountry.CountryID is null)
                entityWithCountry.CountryID = identityClaimProvider.GetCountryID();

            if (entity is IEntityHasRegion<EntityType> entityWithRegion && entityWithRegion.RegionID is null)
                entityWithRegion.RegionID = identityClaimProvider.GetRegionID();

            if (entity is IEntityHasCity<EntityType> entityWithCity && entityWithCity.CityID is null)
                entityWithCity.CityID = identityClaimProvider.GetCityID();

            if (entity is IEntityHasCompany<EntityType> entityWithCompany && entityWithCompany.CompanyID is null)
                entityWithCompany.CompanyID = identityClaimProvider.GetCompanyID();

            if (entity is IEntityHasCompanyBranch<EntityType> entityWithCompanyBranch && entityWithCompanyBranch.CompanyBranchID is null)
                entityWithCompanyBranch.CompanyBranchID = identityClaimProvider.GetCompanyBranchID();
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

        return new ValueTask<EntityType>(entity);
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
    /// <see cref="ShiftRepositoryOptions{EntityType}.DataLevelAccess"/> the declaration is the whole truth for the
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

    private void SetAuditFields(IShiftEntityAudit entity, bool isAdded, long? userId, DateTimeOffset now)
    {
        if (entity.AuditFieldsAreSet)
            return;

        if (isAdded)
        {
            // Insert: fill each stamp only where the caller left it unset, so a manually-provided value is kept
            // rather than overwritten (a default DateTimeOffset / a null id counts as "unset").
            if (entity.CreateDate == default)
                entity.CreateDate = now;

            if (entity.CreatedByUserID is null)
                entity.CreatedByUserID = userId;

            entity.IsDeleted = false;

            if (entity.LastSaveDate == default)
                entity.LastSaveDate = now;

            if (entity.LastSavedByUserID is null)
                entity.LastSavedByUserID = userId;
        }
        else
        {
            // Update: always advance the last-save stamps. A manually-set value is indistinguishable from a value
            // stamped by an earlier save, so there is no reliable way to skip it here.
            entity.LastSaveDate = now;
            entity.LastSavedByUserID = userId;
        }

        entity.AuditFieldsAreSet = true;
    }

    public virtual async Task<int> SaveChangesAsync()
    {
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

        try
        {
            var result = await ProcessEntriesAndSave(now, userId, beforeSaveTasks, afterSaveEntities);

            // Execute all AfterSave hooks - if this fails, transaction will rollback
            var afterSaveTasks = afterSaveEntities.Select(x => this.afterSaveHook!.AfterSaveAsync(x.entity, x.action));
            await Task.WhenAll(afterSaveTasks.Select(vt => vt.AsTask()));

            await transaction.CommitAsync();

            return result;
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    private async Task<int> SaveChangesWithoutTransactionAsync(
        DateTimeOffset now,
        long? userId,
        List<ValueTask> beforeSaveTasks,
        List<(EntityType entity, ActionTypes action)> afterSaveEntities)
    {
        return await ProcessEntriesAndSave(now, userId, beforeSaveTasks, afterSaveEntities);

        // No AfterSave to execute since it's not overridden
    }

    private async Task<int> ProcessEntriesAndSave(
        DateTimeOffset now,
        long? userId,
        List<ValueTask> beforeSaveTasks,
        List<(EntityType entity, ActionTypes action)> afterSaveEntities)
    {
        var entitiesToReload = new List<EntityType>();
        List<PendingIndexedSignal>? allPendingSignals = null;

        foreach (var entry in db.ChangeTracker.Entries())
        {
            var added = entry.State == EntityState.Added;
            var modified = entry.State == EntityState.Modified;

            if (!added && !modified)
                continue;

            // Audit fields are stamped on EVERY changed auditable row in the unit of work — not just this
            // repository's own entity type. A single SaveChanges flushes the whole ChangeTracker, so cascaded
            // children and unrelated entities (any ShiftEntity<T>) must be stamped here too.
            if (entry.Entity is IShiftEntityAudit auditable)
                this.SetAuditFields(auditable, added, userId, now);

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

                var pending = await AttentionPipeline.ProcessEntity(
                    db, entry, entityType, original, actionType, serviceProvider);

                if (pending is not null)
                {
                    allPendingSignals ??= [];
                    allPendingSignals.AddRange(pending);
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

        // Proceed with database save
        int result;
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

        // Reload entities that have navigation properties (Includes)
        if (entitiesToReload.Count > 0 && entityMapper is not null)
        {
            foreach (var trackedEntity in entitiesToReload)
            {
                var freshEntity = await FindAsync(trackedEntity.ID, bypass: RepositoryBypass.All);
                if (freshEntity is not null)
                    entityMapper.CopyEntity(freshEntity, trackedEntity);
            }
        }

        return result;
    }

    public virtual ValueTask<EntityType> DeleteAsync(EntityType entity, bool isHardDelete, long? userId, bool disableDefaultDataLevelAccess, bool disableGlobalFilters)
    {
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
    /// <see cref="DeleteAsync(EntityType, bool, long?, bool, bool)"/> with the named <see cref="RepositoryBypass"/>
    /// vocabulary instead of the positional bool pair. Deliberately <b>non-virtual</b>: it forwards into the
    /// bool-taking virtual, so repositories that override it keep receiving every call regardless of which
    /// overload the caller used (override the bool overload to customize).
    /// </summary>
    public ValueTask<EntityType> DeleteAsync(
        EntityType entity, bool isHardDelete, long? userId, RepositoryBypass bypass = RepositoryBypass.None)
        => DeleteAsync(entity, isHardDelete, userId,
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
