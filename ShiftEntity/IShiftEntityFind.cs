using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System;
using System.Threading.Tasks;
using ShiftSoftware.ShiftEntity.Core.Dtos;

namespace ShiftSoftware.ShiftEntity.Core
{
    public interface IShiftEntityFind<EntityType> where EntityType : ShiftEntity<EntityType>
    {
        public Task<EntityType> FindAsync(Guid id, DateTime? asOf = null);
        public Task<List<RevisionDTO>> GetRevisionsAsync(Guid id);
    }
}
