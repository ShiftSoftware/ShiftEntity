using Microsoft.EntityFrameworkCore;
using System.Threading.Tasks;
using System;
using System.Linq;
using ShiftSoftware.ShiftEntity.Core.Dtos;
using System.Collections.Generic;

namespace ShiftSoftware.ShiftEntity.Core
{
    public interface IShiftFind
    {
        public Task<List<RevisionDTO>> GetRevisionsAsync<T>(DbSet<T> dbSet, Guid id) where T : ShiftEntity<T>;
        public Task<T> FindAsync<T>(DbSet<T> dbSet, Guid id, DateTime? asOf) where T : ShiftEntity<T>;
    }
}
