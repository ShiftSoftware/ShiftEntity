using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System;
using System.Threading.Tasks;
using ShiftSoftware.ShiftEntity.Core.Dtos;

namespace ShiftSoftware.ShiftEntity.Core
{
    public interface IShiftEntityFind<EntityType> where EntityType : ShiftEntity<EntityType>
    {
        public EntityType Find(DbSet<EntityType> dbSet, Guid id, DateTime? asOf = null, List<string> includes = null);
        public Task<EntityType> FindAsync(DbSet<EntityType> dbSet, Guid id, DateTime? asOf = null, List<string> includes = null);

        public Task<List<RevisionDTO>> GetRevisionsAsync(DbSet<EntityType> dbSet, Guid id);
    }
}
