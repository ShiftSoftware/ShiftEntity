using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Core;

public interface IShiftRepositoryAsync<Entity, ListDTO, ViewAndUpsertDTO> :
        IShiftOdataList<Entity, ListDTO>,
        IShiftEntityFind<Entity>,
        IShiftEntityPrepareForReplicationAsync<Entity>,
        IShiftEntityViewAsync<Entity, ViewAndUpsertDTO>,
        IShiftEntityDeleteAsync<Entity>
        where Entity : ShiftEntity<Entity>, new()
        where ListDTO : ShiftEntityDTOBase
{

    void Add(Entity entity);
    Task SaveChangesAsync();
    Message? ResponseMessage { get; set; }
    Dictionary<string, object>? AdditionalResponseData { get; set; }
    public ValueTask<Entity> UpsertAsync(Entity entity, ViewAndUpsertDTO dto, ActionTypes actionType, long? userId = null, Guid? idempotencyKey = null);
}
