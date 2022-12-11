using Microsoft.EntityFrameworkCore;
using System.Threading.Tasks;
using System;
using System.Linq;
using ShiftSoftware.ShiftEntity.Core.Dtos;
using System.Collections.Generic;

namespace ShiftSoftware.ShiftEntity.Core
{
    public interface IShiftFind<T> where T : ShiftEntity<T>
    {
        public Task<List<RevisionDTO>> GetRevisionsAsync(DbSet<T> dbSet, Guid id);
        public Task<T> FindAsync(DbSet<T> dbSet, Guid id, DateTime? asOf = null);
    }
}
