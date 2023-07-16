using AutoMapper;
using Microsoft.EntityFrameworkCore;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Model.Dtos;

namespace ShiftSoftware.EFCore.SqlServer
{
    public class ShiftRepository<DB, EntityType> : ShiftRepository<EntityType>
        where DB : DbContext
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

    public class ShiftRepository<EntityType> where EntityType :
        ShiftEntity<EntityType>
    {
        DbSet<EntityType> dbSet;
        DbContext db;

        public Message ResponseMessage { get; set; }
        public Dictionary<string, object> AdditionalResponseData { get; set; }

        public ShiftRepository(DbContext db, DbSet<EntityType> dbSet)
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
            (long id, DateTime? asOf = null, bool ignoreGlobalFilters = false, List<string> includes = null)
        {
            var q = GetIQueryable(asOf, includes);

            if (ignoreGlobalFilters)
                q = q.IgnoreQueryFilters();

            return await q.FirstOrDefaultAsync(x =>
                    EF.Property<long>(x, nameof(ShiftEntity<EntityType>.ID)) == id
                );
        }

        public virtual async Task<EntityType> FindAsync
            (long id, DateTime? asOf = null, bool ignoreGlobalFilters = false, params Action<IncludeOperations<EntityType>>[] includeOperations)
        {
            List<string> includes = new();

            foreach (var i in includeOperations)
            {
                IncludeOperations<EntityType> operation = new();
                i.Invoke(operation);
                includes.Add(operation.Includes);
            }

            return await FindAsync(id, asOf, ignoreGlobalFilters, includes);
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
    }
}
