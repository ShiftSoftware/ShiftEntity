using AutoMapper;
using EntityFrameworkCore.Triggered;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Flags;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using System.Text;

namespace ShiftSoftware.ShiftEntity.EFCore;

public class ShiftRepository<DB, EntityType, ListDTO, ViewAndUpsertDTO> :
    ShiftRepositoryBase,
    IShiftRepositoryAsync<EntityType, ListDTO, ViewAndUpsertDTO>
    where DB : ShiftDbContext
    where EntityType : ShiftEntity<EntityType>, new()
    where ListDTO : ShiftEntityDTOBase
{
    public readonly DB db;
    internal DbSet<EntityType> dbSet;
    public readonly IMapper mapper;

    public ShiftRepository(DB db, Action<ShiftRepositoryOptions<EntityType>>? shiftRepositoryBuilder = null)
    {
        this.db = db;
        this.dbSet = db.Set<EntityType>();
        this.mapper = db.GetService<IMapper>();

        if (shiftRepositoryBuilder is not null)
        {
            this.ShiftRepositoryOptions = new ShiftRepositoryOptions<EntityType>();

            shiftRepositoryBuilder.Invoke(this.ShiftRepositoryOptions);
        }
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

    public ShiftRepositoryOptions<EntityType>? ShiftRepositoryOptions { get; set; }

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

    private async Task<EntityType?> BaseFindAsync(long id, DateTimeOffset? asOf = null, Guid? idempotencyKey = null)
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

        var q = GetIQueryable(asOf, includes);

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

        return entity;
    }

    public virtual async Task<EntityType?> FindAsync(long id, DateTimeOffset? asOf = null)
    {
        return await BaseFindAsync(id, asOf);
    }

    public virtual async Task<EntityType?> FindByIdempotencyKeyAsync(Guid idempotencyKey)
    {
        return await BaseFindAsync(0, null, idempotencyKey);
    }

    private IQueryable<EntityType> GetIQueryable(DateTimeOffset? asOf, List<string>? includes)
    {
        IQueryable<EntityType> iQueryable;

        if (asOf == null)
            iQueryable = dbSet;
        else
            iQueryable = dbSet.TemporalAsOf(asOf.Value.UtcDateTime);

        if (includes != null)
        {
            foreach (var include in includes)
                iQueryable = iQueryable.Include(include);
        }

        return iQueryable;
    }

    //private IQueryable<EntityType> GetIQueryable(DateTime? asOf, params Action<IncludeOperations<EntityType>>[] includeOperations)
    //{
    //    List<string> includes = new();

    //    foreach (var i in includeOperations)
    //    {
    //        IncludeOperations<EntityType> operation = new();
    //        i.Invoke(operation);
    //        includes.Add(operation.Includes);
    //    };

    //    return GetIQueryable(asOf, includes);
    //}

    public virtual IQueryable<EntityType> GetIQueryable()
    {
        var query = dbSet.AsQueryable();

        if (db?.ShiftDbContextOptions?.UseTemporal ?? false)
        {
            //if (showDeletedRows)
            //{
                //query = dbSet.TemporalAll()
                //.Where(x => !dbSet.Any(p => p.ID == x.ID))
                //.Select(x => new
                //{
                //    Entity = x,
                //    RowNumber = EF.Functions.RowNumber(x.ID, EF.Functions.OrderByDescending(EF.Property<DateTime>(x, "PeriodStart")))
                //})
                //.AsSubQuery()
                //.Where(x => x.RowNumber <= 1)
                //.Select(x => x.Entity);
            //}
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

    public virtual async Task SaveChangesAsync(bool raiseBeforeCommitTriggers = false)
    {
        foreach (var entry in db.ChangeTracker.Entries())
        {
            if (entry.State == EntityState.Added || entry.State == EntityState.Modified)
            {
                if (typeof(EntityType).GetInterfaces().Any(x => x.IsAssignableFrom(typeof(IEntityHasUniqueHash<EntityType>))))
                {
                    var entryWithUniqueHash = (entry.Entity as IEntityHasUniqueHash<EntityType>)!;

                    var uniqueHash = entryWithUniqueHash.CalculateUniqueHash();

                    if (uniqueHash != null)
                    {
                        using var sha256 = System.Security.Cryptography.SHA512.Create();
                        
                        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(uniqueHash));

                        entry.Property("UniqueHash").CurrentValue = hashBytes;
                    }
                }
            }
        }

        if (raiseBeforeCommitTriggers)
        {
            using var tx = db.Database.BeginTransaction();
            var triggerService = db.GetService<ITriggerService>(); // ITriggerService is responsible for creating now trigger sessions (see below)
            var triggerSession = triggerService.CreateSession(db); // A trigger session keeps track of all changes that are relevant within that session. e.g. RaiseAfterSaveTriggers will only raise triggers on changes it discovered within this session (through RaiseBeforeSaveTriggers)

            try
            {
                await db.SaveChangesAsync();
                await triggerSession.RaiseBeforeCommitTriggers();
                await tx.CommitAsync();
                await triggerSession.RaiseAfterCommitTriggers();
            }
            catch
            {
                await triggerSession.RaiseBeforeRollbackTriggers();
                await tx.RollbackAsync();
                await triggerSession.RaiseAfterRollbackTriggers();
                throw;
            }

        }
        else
            await db.SaveChangesAsync();
    }

    public virtual ValueTask<EntityType> DeleteAsync(EntityType entity, bool isHardDelete = false, long? userId = null)
    {
        if (isHardDelete)
            dbSet.Remove(entity);
        else
            entity.MarkAsDeleted();


        return new ValueTask<EntityType>(entity);
    }

    public virtual Task<Stream> PrintAsync(string id)
    {
        throw new NotImplementedException();
    }
}
