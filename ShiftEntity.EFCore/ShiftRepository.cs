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
    private readonly IDefaultDataLevelAccess? defaultDataLevelAccess;
    private readonly IdentityClaimProvider identityClaimProvider;
    private readonly ICurrentUserProvider? currentUserProvider;

    public ShiftRepository(DB db, Action<ShiftRepositoryOptions<EntityType>>? shiftRepositoryBuilder = null)
    {
        this.db = db;
        this.dbSet = db.Set<EntityType>();
        this.mapper = db.GetService<IMapper>();
        this.identityClaimProvider = db.GetService<IdentityClaimProvider>();
        this.currentUserProvider = db.GetService<ICurrentUserProvider>();
        this.ShiftRepositoryOptions = new();

        if (shiftRepositoryBuilder is not null)
        {
            this.ShiftRepositoryOptions.SetCurrentUserProvider(this.currentUserProvider);

            this.ShiftRepositoryOptions.SetTypeAuthService(db.GetService<ITypeAuthService>());

            shiftRepositoryBuilder.Invoke(this.ShiftRepositoryOptions);
        }

        //if (this.ShiftRepositoryOptions.UseDefaultDataLevelAccess)
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

    //public virtual async ValueTask<EntityType> UpdateAsync(EntityType entity, ViewAndUpsertDTO dto, long? userId, bool disableDefaultDataLevelAccess = false)
    //{
    //    var upserted = await UpsertAsync(entity, dto, ActionTypes.Update, userId, null);

    //    if (!disableDefaultDataLevelAccess)
    //    {
    //        var canWrite = this.defaultDataLevelAccess!.HasDefaultDataLevelAccess(
    //            this.ShiftRepositoryOptions.DefaultDataLevelAccessOptions,
    //            entity,
    //            TypeAuth.Core.Access.Write
    //        );

    //        if (!canWrite)
    //            throw new ShiftEntityException(new Message("Forbidden", "Can Not Update Item"), (int)HttpStatusCode.Forbidden);
    //    }

    //    return upserted;
    //}

    //public virtual async ValueTask<EntityType> CreateAsync(ViewAndUpsertDTO dto, long? userId, Guid? idempotencyKey, bool disableDefaultDataLevelAccess = false)
    //{
    //    var entity = await UpsertAsync(new EntityType(), dto, ActionTypes.Insert, userId, idempotencyKey);

    //    if (entity is IEntityHasCountry<EntityType> entityWithCountry && entityWithCountry.CountryID is null)
    //        entityWithCountry.CountryID = identityClaimProvider.GetCountryID();

    //    if (entity is IEntityHasRegion<EntityType> entityWithRegion && entityWithRegion.RegionID is null)
    //        entityWithRegion.RegionID = identityClaimProvider.GetRegionID();

    //    if (entity is IEntityHasCity<EntityType> entityWithCity && entityWithCity.CityID is null)
    //        entityWithCity.CityID = identityClaimProvider.GetCityID();

    //    if (entity is IEntityHasCompany<EntityType> entityWithCompany && entityWithCompany.CompanyID is null)
    //        entityWithCompany.CompanyID = identityClaimProvider.GetCompanyID();

    //    if (entity is IEntityHasCompanyBranch<EntityType> entityWithCompanyBranch && entityWithCompanyBranch.CompanyBranchID is null)
    //        entityWithCompanyBranch.CompanyBranchID = identityClaimProvider.GetCompanyBranchID();

    //    if (!disableDefaultDataLevelAccess)
    //    {
    //        var canWrite = this.defaultDataLevelAccess!.HasDefaultDataLevelAccess(
    //            this.ShiftRepositoryOptions.DefaultDataLevelAccessOptions,
    //            entity,
    //            TypeAuth.Core.Access.Write
    //        );

    //        if (!canWrite)
    //            throw new ShiftEntityException(new Message("Forbidden", "Can Not Create Item"), (int)HttpStatusCode.Forbidden);
    //    }

    //    return entity;
    //}

    public virtual ValueTask<EntityType> UpsertAsync(
        EntityType entity, ViewAndUpsertDTO dto,
        ActionTypes actionType,
        long? userId = null,
        Guid? idempotencyKey = null
    )
    {
        entity = mapper.Map(dto, entity);

        var now = DateTime.UtcNow;

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

        var canWrite = this.defaultDataLevelAccess!.HasDefaultDataLevelAccess(
            this.ShiftRepositoryOptions.DefaultDataLevelAccessOptions,
            entity,
            TypeAuth.Core.Access.Write
        );

        if (!canWrite)
        {
            var messageText = actionType == ActionTypes.Insert ? "Can Not Create Item" : "Can Not Update Item";

            throw new ShiftEntityException(new Message("Forbidden", messageText), (int)HttpStatusCode.Forbidden);
        }

        return new ValueTask<EntityType>(entity);
    }

    public Message? ResponseMessage { get; set; }
    public Dictionary<string, object>? AdditionalResponseData { get; set; }

    //public virtual EntityType Find(long id, DateTime? asOf = null, List<string> includes = null)
    //{
    //    return GetIQueryable(asOf, includes)
    //        .FirstOrDefault(x =>
    //            EF.Property<long>(x, nameof(ShiftEntity<EntityType>.ID)) == id
    //        );
    //}

    //public virtual EntityType Find(long id, DateTime? asOf = null, params Action<IncludeOperations<EntityType>>[] includeOperations)
    //{
    //    List<string> includes = new();

    //    foreach (var i in includeOperations)
    //    {
    //        IncludeOperations<EntityType> operation = new();
    //        i.Invoke(operation);
    //        includes.Add(operation.Includes);
    //    }

    //    return Find(id, asOf, includes);
    //}

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
                this.ShiftRepositoryOptions.DefaultDataLevelAccessOptions,
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

    private void SetAuditFields<T>(ShiftEntity<T> entity, bool isAdded, long? userId, DateTime now)
        where T : ShiftEntity<EntityType>, new()
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

    public virtual async Task SaveChangesAsync()
    {
        var now = DateTime.UtcNow;

        long? userId = this.currentUserProvider?.GetUser()?.GetUserID();

        foreach (var entry in db.ChangeTracker.Entries())
        {
            var added = entry.State == EntityState.Added;
            var modified = entry.State == EntityState.Modified;

            if (!added && !modified)
                continue;

            if (entry.Entity is ShiftEntity<EntityType> entity)
            {
                this.SetAuditFields(entity, added, userId, now);
            }

            if (typeof(EntityType).GetInterfaces().Any(x => x.IsAssignableFrom(typeof(IEntityHasUniqueHash<EntityType>))))
            {
                var entryWithUniqueHash = entry.Entity as IEntityHasUniqueHash<EntityType>;

                if (entryWithUniqueHash is null)
                    continue;

                var uniqueHash = entryWithUniqueHash.CalculateUniqueHash();

                if (uniqueHash != null)
                {
                    using var sha256 = System.Security.Cryptography.SHA256.Create();

                    var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(uniqueHash));

                    entry.Property("UniqueHash").CurrentValue = hashBytes;
                }
            }
        }

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException dbUpdateException)
        {
            if (dbUpdateException.InnerException is SqlException sqlException)
            {
                //2601: This error occurs when you attempt to put duplicate index values into a column or columns that have a unique index.
                //Message looks something like: {"Cannot insert duplicate key row in object 'dbo.Countries' with unique index 'IX_Countries_IdempotencyKey'. The duplicate key value is (88320ba8-345f-410a-9aa4-3e8a7112c040)."}

                var error = sqlException
                    .Errors
                    .OfType<SqlError>()
                    .FirstOrDefault(se => se.Number == 2601);

                //Check if the error is from duplicate idempotency key
                if ((error?.Message?.Contains(nameof(IEntityHasIdempotencyKey<EntityType>.IdempotencyKey))) ?? false)
                    throw new DuplicateIdempotencyKeyException(error.Message);

                //Check if the error is from duplicate unique hash
                if ((error?.Message?.Contains(nameof(IEntityHasUniqueHash.UniqueHash))) ?? false)
                    throw new ShiftEntityException(new Message("Conflict", "An item with the same Unique Fields already exists."), (int)HttpStatusCode.Conflict);
            }
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

        this.SetAuditFields(entity, false, userId, DateTime.UtcNow);

        return new ValueTask<EntityType>(entity);
    }

    public virtual Task<Stream> PrintAsync(string id)
    {
        throw new NotImplementedException();
    }
}
