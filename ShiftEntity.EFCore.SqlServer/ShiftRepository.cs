using AutoMapper;
using Microsoft.EntityFrameworkCore;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using Thinktecture;

namespace ShiftSoftware.EFCore.SqlServer
{
    public class ShiftRepository<DB, EntityType, ListDTO, ViewDTO, UpsertDTO> : ShiftRepository<DB, EntityType>
        where DB : ShiftDbContext
        where EntityType : ShiftEntity<EntityType>
    {
        public ShiftRepository(DB db, DbSet<EntityType> dbSet, IMapper mapper) : base(db, dbSet, mapper)
        {
        }

        public virtual IQueryable<ListDTO> OdataList(bool showDeletedRows = false)
        {
            return mapper.ProjectTo<ListDTO>(GetIQueryable(showDeletedRows).AsNoTracking());
        }

        public virtual ValueTask<ViewDTO> ViewAsync(EntityType entity)
        {
            return new ValueTask<ViewDTO>(mapper.Map<ViewDTO>(entity));
        }

        public virtual ValueTask<EntityType> UpsertAsync(EntityType entity, UpsertDTO dto, ActionTypes actionType, long? userId = null)
        {
            entity = mapper.Map<UpsertDTO, EntityType>(dto, entity);

            return new ValueTask<EntityType>(entity);
        }
    }

    public class ShiftRepository<DB, EntityType> : ShiftRepository<EntityType>
        where DB : ShiftDbContext
        where EntityType : ShiftEntity<EntityType>
    {
        public readonly DB db;
        public readonly IMapper mapper;
        public ShiftRepository(DB db, DbSet<EntityType> dbSet, IMapper mapper) : base(db, dbSet)
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

        public Message ResponseMessage { get; set; }
        public Dictionary<string, object> AdditionalResponseData { get; set; }

        public ShiftRepository(ShiftDbContext db, DbSet<EntityType> dbSet)
        {
            this.db = db;
            this.dbSet = dbSet;
        }

        public virtual EntityType Find(long id, DateTime? asOf = null, List<string> includes = null)
        {
            return GetIQueryable(asOf, includes)
                .FirstOrDefault(x =>
                    EF.Property<long>(x, nameof(ShiftEntity<EntityType>.ID)) == id
                );
        }

        public virtual EntityType Find(long id, DateTime? asOf = null, params Action<IncludeOperations<EntityType>>[] includeOperations)
        {
            List<string> includes = new();

            foreach (var i in includeOperations)
            {
                IncludeOperations<EntityType> operation = new();
                i.Invoke(operation);
                includes.Add(operation.Includes);
            }

            return Find(id, asOf, includes);
        }

        public virtual async Task<EntityType> FindAsync
            (long id, DateTime? asOf = null, List<string> includes = null)
        {
            var q = GetIQueryable(asOf, includes);

            return await q.FirstOrDefaultAsync(x =>
                    EF.Property<long>(x, nameof(ShiftEntity<EntityType>.ID)) == id
                );
        }

        public virtual async Task<EntityType> FindAsync
            (long id, DateTime? asOf = null)
        {
            return await FindAsync(id, asOf, new List<string> { });
        }

        public virtual async Task<EntityType> FindAsync
            (long id, DateTime? asOf = null, params Action<IncludeOperations<EntityType>>[] includeOperations)
        {
            List<string> includes = new();

            foreach (var i in includeOperations)
            {
                IncludeOperations<EntityType> operation = new();
                i.Invoke(operation);
                includes.Add(operation.Includes);
            }

            return await FindAsync(id, asOf, includes);
        }

        private IQueryable<EntityType> GetIQueryable(DateTime? asOf, List<string> includes)
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

        private IQueryable<EntityType> GetIQueryable(DateTime? asOf, params Action<IncludeOperations<EntityType>>[] includeOperations)
        {
            List<string> includes = new();

            foreach (var i in includeOperations)
            {
                IncludeOperations<EntityType> operation = new();
                i.Invoke(operation);
                includes.Add(operation.Includes);
            };

            return GetIQueryable(asOf, includes);
        }

        protected IQueryable<EntityType> GetIQueryable(bool showDeletedRows=false)
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
            dbSet.Add(entity);
        }

        public virtual async Task SaveChangesAsync()
        {
            await db.SaveChangesAsync();
        }

        public virtual ValueTask<EntityType> DeleteAsync(EntityType entity, bool isHardDelete = false, long? userId = null)
        {
            if (isHardDelete)
                dbSet.Remove(entity);
            else
                entity.DeleteShiftEntity(userId);


            return new ValueTask<EntityType>(entity);
        }
    }
}
