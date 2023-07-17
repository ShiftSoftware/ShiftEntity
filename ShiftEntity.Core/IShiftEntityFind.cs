﻿using ShiftSoftware.ShiftEntity.Model.Dtos;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Core
{
    public interface IShiftEntityFind<EntityType> where EntityType : ShiftEntity<EntityType>
    {
        public Task<EntityType> FindAsync(long id, DateTime? asOf = null, bool ignoreGlobalFilters = false);
        public Task<List<RevisionDTO>> GetRevisionsAsync(long id);
    }
}