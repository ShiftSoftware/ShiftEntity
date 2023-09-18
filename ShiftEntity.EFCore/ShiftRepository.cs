using AutoMapper;
using EntityFrameworkCore.Triggered;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using Thinktecture;

namespace ShiftSoftware.ShiftEntity.EFCore
{
    public class ShiftRepository<DB, EntityType, ListDTO, ViewDTO, UpsertDTO> : ShiftRepository<DB, EntityType>
        where DB : ShiftDbContext
        where EntityType : ShiftEntity<EntityType>
    {
        public ShiftRepository(DB db, DbSet<EntityType> dbSet, IMapper mapper, Action<ShiftRepositoryOptions<EntityType>>? shiftRepositoryBuilder = null) : base(db, dbSet, mapper, shiftRepositoryBuilder)
        {
        }

        public virtual IQueryable<ListDTO> OdataList(bool showDeletedRows = false, IQueryable<EntityType>? queryable = null)
        {
            if (queryable is null)
                queryable = GetIQueryableForOData(showDeletedRows);

            return mapper.ProjectTo<ListDTO>(queryable.AsNoTracking());
        }

        public virtual ValueTask<ViewDTO> ViewAsync(EntityType entity)
        {
            return new ValueTask<ViewDTO>(mapper.Map<ViewDTO>(entity));
        }

        public virtual ValueTask<EntityType> UpsertAsync(EntityType entity, UpsertDTO dto, ActionTypes actionType, long? userId = null)
        {
            entity = mapper.Map(dto, entity);

            return new ValueTask<EntityType>(entity);
        }
    }

    public class ShiftRepository<DB, EntityType> : ShiftRepository<EntityType>
        where DB : ShiftDbContext
        where EntityType : ShiftEntity<EntityType>
    {
        public readonly DB db;
        public readonly IMapper mapper;
        public ShiftRepository(DB db, DbSet<EntityType> dbSet, IMapper mapper, Action<ShiftRepositoryOptions<EntityType>>? shiftRepositoryBuilder = null) : base(db, dbSet, shiftRepositoryBuilder)
        {
            this.db = db;
            this.mapper = mapper;
        }
    }

    public class ShiftRepository<EntityType> : IShiftEntityDeleteAsync<EntityType> where EntityType :
        ShiftEntity<EntityType>
    {
        internal DbSet<EntityType> dbSet;
        ShiftDbContext db;

        public Message? ResponseMessage { get; set; }
        public Dictionary<string, object>? AdditionalResponseData { get; set; }

        public ShiftRepositoryOptions<EntityType>? ShiftRepositoryOptions { get; set; }

        public ShiftRepository(ShiftDbContext db, DbSet<EntityType> dbSet, Action<ShiftRepositoryOptions<EntityType>>? shiftRepositoryBuilder = null)
        {
            this.db = db;
            this.dbSet = dbSet;

            if (shiftRepositoryBuilder is not null)
            {
                this.ShiftRepositoryOptions = new ShiftRepositoryOptions<EntityType>();

                shiftRepositoryBuilder.Invoke(this.ShiftRepositoryOptions);
            }
        }

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

        private async Task<EntityType?> BaseFindAsync(long id, DateTime? asOf = null)
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

            var entity = await q.FirstOrDefaultAsync(x =>
                EF.Property<long>(x, nameof(ShiftEntity<EntityType>.ID)) == id
            );

            if (entity is not null && includes?.Count > 0)
                entity.ReloadAfterSave = true;

            return entity;
        }

        public async Task<EntityType?> FindAsync(long id, DateTime? asOf = null)
        {
            return await BaseFindAsync(id, asOf);
        }

        private IQueryable<EntityType> GetIQueryable(DateTime? asOf, List<string>? includes)
        {
            IQueryable<EntityType> iQueryable;

            if (asOf == null)
                iQueryable = dbSet;
            else
                iQueryable = dbSet.TemporalAsOf(asOf.Value);

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

        public IQueryable<EntityType> GetIQueryableForOData(bool showDeletedRows = false)
        {
            var query = dbSet.AsQueryable();

            if (db?.ShiftDbContextOptions?.UseTemporal ?? false)
            {
                if (showDeletedRows)
                {
                    query = dbSet.TemporalAll()
                    .Where(x => !dbSet.Any(p => p.ID == x.ID))
                    .Select(x => new
                    {
                        Entity = x,
                        RowNumber = EF.Functions.RowNumber(x.ID, EF.Functions.OrderByDescending(EF.Property<DateTime>(x, "PeriodStart")))
                    })
                    .AsSubQuery()
                    .Where(x => x.RowNumber <= 1)
                    .Select(x => x.Entity);
                }
            }

            return query;
        }

        public virtual async Task<List<RevisionDTO>> GetRevisionsAsync(long id)
        {
            var items = (await dbSet
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
                    .OrderByDescending(x => x.ValidTo)
                    .ToListAsync())
                    .Select(x => new RevisionDTO
                    {
                        ID = x.ID.ToString(),
                        ValidFrom = x.ValidFrom,
                        ValidTo = x.ValidTo,
                        SavedByUserID = x.SavedByUserID == null ? null : x.SavedByUserID.ToString(),
                    }).ToList();

            return items;
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
    }
}
