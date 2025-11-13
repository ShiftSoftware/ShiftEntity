using AutoMapper;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Flags;
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
    public readonly DB db;
    internal DbSet<EntityType> dbSet;
    public readonly IMapper mapper;
    public ShiftRepositoryOptions<EntityType> ShiftRepositoryOptions { get; set; }
    public readonly IDefaultDataLevelAccess? defaultDataLevelAccess;
    public readonly IdentityClaimProvider identityClaimProvider;
    public readonly ICurrentUserProvider? currentUserProvider;
    private readonly bool _hasUniqueHashInterface;

    private readonly IShiftEntityHasBeforeSaveHook<EntityType>? beforeSaveHook = null;
    private readonly IShiftEntityHasAfterSaveHook<EntityType>? afterSaveHook = null;

    public ShiftRepository(DB db, Action<ShiftRepositoryOptions<EntityType>>? shiftRepositoryBuilder = null)
    {
        this.db = db;
        this.dbSet = db.Set<EntityType>();
        this.mapper = db.GetService<IMapper>();
        this.identityClaimProvider = db.GetService<IdentityClaimProvider>();
        this.currentUserProvider = db.GetService<ICurrentUserProvider>();
        this.ShiftRepositoryOptions = new();

        _hasUniqueHashInterface = typeof(EntityType).GetInterfaces()
            .Any(x => x.IsAssignableFrom(typeof(IEntityHasUniqueHash<EntityType>)));

        if (this is IShiftEntityHasBeforeSaveHook<EntityType> beforeSaveHook)
            this.beforeSaveHook = beforeSaveHook;

        if (this is IShiftEntityHasAfterSaveHook<EntityType> afterSaveHook)
            this.afterSaveHook = afterSaveHook;

        if (shiftRepositoryBuilder is not null)
        {
            this.ShiftRepositoryOptions.SetCurrentUserProvider(this.currentUserProvider);

            this.ShiftRepositoryOptions.SetTypeAuthService(db.GetService<ITypeAuthService>());

            shiftRepositoryBuilder.Invoke(this.ShiftRepositoryOptions);
        }

        this.defaultDataLevelAccess = db.GetService<IDefaultDataLevelAccess>();
    }

    public virtual async ValueTask<IQueryable<ListDTO>> OdataList(IQueryable<EntityType>? queryable = null)
    {
        if (queryable is null)
            queryable = await GetIQueryable();

        return mapper.ProjectTo<ListDTO>(queryable.AsNoTracking());
    }

    public virtual ValueTask<ViewAndUpsertDTO> ViewAsync(EntityType entity)
    {
        return new ValueTask<ViewAndUpsertDTO>(mapper.Map<ViewAndUpsertDTO>(entity));
    }

    public virtual ValueTask<EntityType> UpsertAsync(
        EntityType entity, ViewAndUpsertDTO dto,
        ActionTypes actionType,
        long? userId = null,
        Guid? idempotencyKey = null
    )
    {
        entity = mapper.Map(dto, entity);

        var now = DateTimeOffset.UtcNow;

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

        if (this.defaultDataLevelAccess is not null)
        {
            var canWrite = this.defaultDataLevelAccess.HasDefaultDataLevelAccess(
                this.ShiftRepositoryOptions.DefaultDataLevelAccessOptions,
                entity,
                TypeAuth.Core.Access.Write
            );

            if (!canWrite)
            {
                var messageText = actionType == ActionTypes.Insert ? "Can Not Create Item" : "Can Not Update Item";

                throw new ShiftEntityException(new Message("Forbidden", messageText), (int)HttpStatusCode.Forbidden);
            }
        }

        return new ValueTask<EntityType>(entity);
    }

    public Message? ResponseMessage { get; set; }
    public Dictionary<string, object>? AdditionalResponseData { get; set; }

    private async Task<EntityType?> BaseFindAsync(long id, DateTimeOffset? asOf = null, Guid? idempotencyKey = null, bool disableDefaultDataLevelAccess = false, bool disableGlobalFilters = false)
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

        var q = await GetIQueryable(asOf, includes, disableDefaultDataLevelAccess, disableGlobalFilters);

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
            var canRead = this.defaultDataLevelAccess!.HasDefaultDataLevelAccess(
                this.ShiftRepositoryOptions!.DefaultDataLevelAccessOptions,
                entity,
                TypeAuth.Core.Access.Read
            );

            if (!canRead)
                throw new ShiftEntityException(new Message("Forbidden", "Can Not Read Item"), (int)HttpStatusCode.Forbidden);
        }

        return entity;
    }

    public virtual async Task<EntityType?> FindAsync(long id, DateTimeOffset? asOf = null, bool disableDefaultDataLevelAccess = false, bool disableGlobalFilters = false)
    {
        return await BaseFindAsync(id, asOf, null, disableDefaultDataLevelAccess, disableGlobalFilters);
    }

    public virtual async Task<EntityType?> FindByIdempotencyKeyAsync(Guid idempotencyKey)
    {
        return await BaseFindAsync(0, null, idempotencyKey);
    }

    public virtual async ValueTask<IQueryable<EntityType>> GetIQueryable(DateTimeOffset? asOf = null, List<string>? includes = null, bool disableDefaultDataLevelAccess = false, bool disableGlobalFilters = false)
    {
        var query = asOf is null ? dbSet.AsQueryable() : dbSet.TemporalAsOf(asOf.Value.UtcDateTime);

        if (includes is not null)
        {
            foreach (var include in includes)
                query = query.Include(include);
        }

        if (!disableDefaultDataLevelAccess)
            query = this.defaultDataLevelAccess!.ApplyDefaultDataLevelFilters(this.ShiftRepositoryOptions.DefaultDataLevelAccessOptions, query);

        if (!disableGlobalFilters)
            query = await query.ApplyGlobalRepositoryFiltersAsync(this.ShiftRepositoryOptions.GlobalRepositoryFilters);

        return query;
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

    private void SetAuditFields(ShiftEntity<EntityType> entity, bool isAdded, long? userId, DateTimeOffset now)
    {
        if (entity.AuditFieldsAreSet)
            return;

        if (isAdded)
        {
            entity.CreateDate = now;
            entity.IsDeleted = false;
            entity.CreatedByUserID = userId;
        }

        entity.LastSaveDate = now;
        entity.LastSavedByUserID = userId;

        entity.AuditFieldsAreSet = true;
    }

    public virtual async Task<int> SaveChangesAsync()
    {
        var now = DateTimeOffset.UtcNow;
        long? userId = this.currentUserProvider?.GetUser()?.GetUserID();
        var beforeSaveTasks = new List<ValueTask>();
        var afterSaveEntities = new List<(EntityType entity, ActionTypes action)>();

        // Only use explicit transaction if AfterSave is overridden
        if (this.afterSaveHook is not null)
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
        foreach (var entry in db.ChangeTracker.Entries())
        {
            var added = entry.State == EntityState.Added;
            var modified = entry.State == EntityState.Modified;

            if (!added && !modified)
                continue;

            // Type-safe entity check
            if (entry.Entity is not ShiftEntity<EntityType> entity)
                continue;

            this.SetAuditFields(entity, added, userId, now);

            // Only process unique hash if the interface is implemented
            if (_hasUniqueHashInterface && entity is IEntityHasUniqueHash<EntityType> entryWithUniqueHash)
            {
                var uniqueHash = entryWithUniqueHash.CalculateUniqueHash();
                if (uniqueHash is not null)
                {
                    using var sha256 = System.Security.Cryptography.SHA256.Create();
                    var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(uniqueHash));
                    entry.Property("UniqueHash").CurrentValue = hashBytes;
                }
            }

            if (entry.Entity is not EntityType entityType)
                continue;

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
        }

        // Execute all BeforeSave hooks only if there are any
        if (beforeSaveTasks.Count > 0)
        {
            await Task.WhenAll(beforeSaveTasks.Select(vt => vt.AsTask()));
        }

        // Proceed with database save
        try
        {
            return await db.SaveChangesAsync();
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
    }

    public virtual ValueTask<EntityType> DeleteAsync(EntityType entity, bool isHardDelete = false, long? userId = null, bool disableDefaultDataLevelAccess = false)
    {
        if (!disableDefaultDataLevelAccess)
        {
            var canRead = this.defaultDataLevelAccess!.HasDefaultDataLevelAccess(
                this.ShiftRepositoryOptions.DefaultDataLevelAccessOptions,
                entity,
                TypeAuth.Core.Access.Delete
            );

            if (!canRead)
                throw new ShiftEntityException(new Message("Forbidden", "Can Not Delete Item"), (int)HttpStatusCode.Forbidden);
        }

        entity.IsDeleted = true;

        this.SetAuditFields(entity, false, userId, DateTimeOffset.UtcNow);

        return new ValueTask<EntityType>(entity);
    }

    public virtual Task<Stream> PrintAsync(string id)
    {
        throw new NotImplementedException();
    }
}