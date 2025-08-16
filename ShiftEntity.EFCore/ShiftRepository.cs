using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Flags;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using System.Net;
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
    private readonly IDefaultDataLevelAccess? defaultDataLevelAccess;
    private readonly IIdentityClaimProvider identityClaimProvider;

    public ShiftRepository(DB db, Action<ShiftRepositoryOptions<EntityType>>? shiftRepositoryBuilder = null)
    {
        this.db = db;
        this.dbSet = db.Set<EntityType>();
        this.mapper = db.GetService<IMapper>();
        this.identityClaimProvider = db.GetService<IIdentityClaimProvider>();
        this.ShiftRepositoryOptions = new();

        if (shiftRepositoryBuilder is not null)
        {
            shiftRepositoryBuilder.Invoke(this.ShiftRepositoryOptions);
        }

        if (this.ShiftRepositoryOptions.UseDefaultDataLevelAccess)
            this.defaultDataLevelAccess = db.GetService<IDefaultDataLevelAccess>();
    }

    public virtual IQueryable<ListDTO> OdataList(IQueryable<EntityType>? queryable = null)
    {
        if (queryable is null)
            queryable = GetIQueryable();

        return mapper.ProjectTo<ListDTO>(queryable.AsNoTracking());
    }

    public virtual ValueTask<ViewAndUpsertDTO> ViewAsync(EntityType entity)
    {
        return new ValueTask<ViewAndUpsertDTO>(mapper.Map<ViewAndUpsertDTO>(entity));
    }

    public virtual async ValueTask<EntityType> UpdateAsync(EntityType entity, ViewAndUpsertDTO dto, long? userId, bool disableDefaultDataLevelAccess = false)
    {
        var upserted = await UpsertAsync(entity, dto, ActionTypes.Update, userId, null);

        upserted.LastSaveDate = DateTime.Now;

        upserted.LastSavedByUserID = userId;

        if (!disableDefaultDataLevelAccess && this.ShiftRepositoryOptions.UseDefaultDataLevelAccess)
        {
            var canWrite = this.defaultDataLevelAccess!.HasDefaultDataLevelAccess(
                this.ShiftRepositoryOptions.DefaultDataLevelAccessOptions,
                entity,
                TypeAuth.Core.Access.Write
            );

            if (!canWrite)
                throw new ShiftEntityException(new Message("Forbidden", "Can Not Update Item"), (int)HttpStatusCode.Forbidden);
        }

        return upserted;
    }

    public virtual async ValueTask<EntityType> CreateAsync(ViewAndUpsertDTO dto, long? userId, Guid? idempotencyKey, bool disableDefaultDataLevelAccess = false)
    {
        var entity = await UpsertAsync(new EntityType(), dto, ActionTypes.Insert, userId, idempotencyKey);

        var now = DateTime.UtcNow;

        //if (entity.LastSaveDate == default)
            entity.LastSaveDate = now;

        //if (entity.CreateDate == default)
            entity.CreateDate = now;

        entity.IsDeleted = false;

        entity.CreatedByUserID = userId;
        entity.LastSavedByUserID = userId;

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

        if (!disableDefaultDataLevelAccess && this.ShiftRepositoryOptions.UseDefaultDataLevelAccess)
        {
            var canWrite = this.defaultDataLevelAccess!.HasDefaultDataLevelAccess(
                this.ShiftRepositoryOptions.DefaultDataLevelAccessOptions,
                entity,
                TypeAuth.Core.Access.Write
            );

            if (!canWrite)
                throw new ShiftEntityException(new Message("Forbidden", "Can Not Create Item"), (int)HttpStatusCode.Forbidden);
        }

        return entity;
    }

    public virtual ValueTask<EntityType> UpsertAsync(EntityType entity, ViewAndUpsertDTO dto, ActionTypes actionType, long? userId = null, Guid? idempotencyKey = null)
    {
        entity = mapper.Map(dto, entity);

        if (idempotencyKey != null)
        {
            (entity as IEntityHasIdempotencyKey<EntityType>)!.IdempotencyKey = idempotencyKey;
        }

        return new ValueTask<EntityType>(entity);
    }

    public Message? ResponseMessage { get; set; }
    public Dictionary<string, object>? AdditionalResponseData { get; set; }

    public ShiftRepositoryOptions<EntityType> ShiftRepositoryOptions { get; set; }

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

    private async Task<EntityType?> BaseFindAsync(long id, DateTimeOffset? asOf = null, Guid? idempotencyKey = null, bool disableDefaultDataLevelAccess = false)
    {
        List<string>? includes = null;

        if (ShiftRepositoryOptions is not null)
        {
            includes = new();

            foreach (var i in ShiftRepositoryOptions.IncludeOperations)
            {
                IncludeOperations<EntityType> operation = new();
                i.Invoke(operation);
                includes.Add(operation.Includes);
            }
        }

        var q = GetIQueryable(asOf, includes, disableDefaultDataLevelAccess);

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

        if (!disableDefaultDataLevelAccess && (this.ShiftRepositoryOptions?.UseDefaultDataLevelAccess ?? false))
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

    public virtual async Task<EntityType?> FindAsync(long id, DateTimeOffset? asOf = null, bool disableDefaultDataLevelAccess = false)
    {
        return await BaseFindAsync(id, asOf, null, disableDefaultDataLevelAccess);
    }

    public virtual async Task<EntityType?> FindByIdempotencyKeyAsync(Guid idempotencyKey)
    {
        return await BaseFindAsync(0, null, idempotencyKey);
    }

    public virtual IQueryable<EntityType> GetIQueryable(DateTimeOffset? asOf = null, List<string>? includes = null, bool disableDefaultDataLevelAccess = false)
    {
        var query = asOf is null ? dbSet.AsQueryable() : dbSet.TemporalAsOf(asOf.Value.UtcDateTime);

        if (includes is not null)
        {
            foreach (var include in includes)
                query = query.Include(include);
        }

        if (!disableDefaultDataLevelAccess && this.ShiftRepositoryOptions.UseDefaultDataLevelAccess)
            query = this.defaultDataLevelAccess!.ApplyDefaultDataLevelFilters(this.ShiftRepositoryOptions.DefaultDataLevelAccessOptions, query);

        query = this.ApplyGloballFilters(query);
        
        return query;
    }

    private IQueryable<EntityType> ApplyGloballFilters(IQueryable<EntityType> query)
    {
        if (this.ShiftRepositoryOptions.GlobalFilters.Count == 0)
            return query;

        foreach (var filter in this.ShiftRepositoryOptions.GlobalFilters)
        {
            var expression = filter.GetFilterExpression<EntityType>();

            if (expression != null)
                query = query.Where(expression);
        }

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

    public virtual async Task SaveChangesAsync()
    {
        foreach (var entry in db.ChangeTracker.Entries())
        {
            if (entry.State == EntityState.Added || entry.State == EntityState.Modified)
            {
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
        }

        await db.SaveChangesAsync();
    }

    public virtual ValueTask<EntityType> DeleteAsync(EntityType entity, bool isHardDelete = false, long? userId = null, bool disableDefaultDataLevelAccess = false)
    {
        if (!disableDefaultDataLevelAccess && this.ShiftRepositoryOptions.UseDefaultDataLevelAccess)
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

        entity.LastSaveDate = DateTime.UtcNow;

        entity.LastSavedByUserID = userId;

        return new ValueTask<EntityType>(entity);
    }

    public virtual Task<Stream> PrintAsync(string id)
    {
        throw new NotImplementedException();
    }
}
