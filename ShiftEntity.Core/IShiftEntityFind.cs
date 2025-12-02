using ShiftSoftware.ShiftEntity.Model.Dtos;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Core;

public interface IShiftEntityFind<EntityType> where EntityType : ShiftEntity<EntityType>
{
    public Task<EntityType?> FindAsync(
        long id,
        DateTimeOffset? asOf,
        bool disableDefaultDataLevelAccess,
        bool disableGlobalFilters
    );
    public Task<EntityType?> FindByIdempotencyKeyAsync(
        Guid idempotencyKey,
        DateTimeOffset? asOf,
        bool disableDefaultDataLevelAccess,
        bool disableGlobalFilters
    );
    public IQueryable<RevisionDTO> GetRevisionsAsync(long id);
    public Task<Stream> PrintAsync(string id);
}