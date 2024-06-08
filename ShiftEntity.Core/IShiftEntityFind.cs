using ShiftSoftware.ShiftEntity.Model.Dtos;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Core;

public interface IShiftEntityFind<EntityType> where EntityType : ShiftEntity<EntityType>
{
    public Task<EntityType?> FindAsync(long id, DateTimeOffset? asOf = null);
    public Task<List<RevisionDTO>> GetRevisionsAsync(long id);
    public Task<Stream> PrintAsync(string id);
    public Task<EntityType?> FindByIdempotencyKeyAsync(Guid idempotencyKey);
}
