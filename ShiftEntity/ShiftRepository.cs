using Microsoft.EntityFrameworkCore;
using ShiftSoftware.ShiftEntity.Core.Dtos;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System;

namespace ShiftSoftware.ShiftEntity.Core
{
    public class ShiftRepository<EntityType> where EntityType : ShiftEntity<EntityType>
    {
        public EntityType Find(DbSet<EntityType> dbSet, Guid id, DateTime? asOf = null, List<string> includes = null)
        {
            return GetIQueryable(dbSet, asOf, includes)
                .FirstOrDefault(x =>
                    EF.Property<Guid>(x, nameof(ShiftEntity<EntityType>.ID)) == id
                );
        }

        public async Task<EntityType> FindAsync(DbSet<EntityType> dbSet, Guid id, DateTime? asOf = null, List<string> includes = null)
        {
            return await GetIQueryable(dbSet, asOf, includes)
                .FirstOrDefaultAsync(x =>
                    EF.Property<Guid>(x, nameof(ShiftEntity<EntityType>.ID)) == id
                );
        }

        private IQueryable<EntityType> GetIQueryable(DbSet<EntityType> dbSet, DateTime? asOf, List<string> includes)
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

        public async Task<List<RevisionDTO>> GetRevisionsAsync(DbSet<EntityType> dbSet, Guid id)
        {
            var items = await dbSet
                    .TemporalAll()
                    .Where(x => EF.Property<Guid>(x, nameof(ShiftEntity<EntityType>.ID)) == id)
                    .Select(x => new RevisionDTO
                    {
                        ValidFrom = EF.Property<DateTime>(x, "PeriodStart"),
                        ValidTo = EF.Property<DateTime>(x, "PeriodEnd"),
                        SavedByUserID = EF.Property<Guid?>(x, nameof(ShiftEntity<EntityType>.LastSavedByUserID)),
                    })
                    .OrderByDescending(x => x.ValidTo)
                    .ToListAsync();

            return items;
        }
    }
}
