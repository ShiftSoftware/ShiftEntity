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
        IShiftEntityCreateAsync<Entity, ViewAndUpsertDTO>,
        IShiftEntityUpdateAsync<Entity, ViewAndUpsertDTO>,
        IShiftEntityDeleteAsync<Entity>
        where Entity : ShiftEntity<Entity>, new()
        where ListDTO : ShiftEntityDTOBase
{

    void Add(Entity entity);
    Task SaveChangesAsync(bool raiseBeforeCommitTriggers = false);

    Message? ResponseMessage { get; set; }
    Dictionary<string, object>? AdditionalResponseData { get; set; }

    ValueTask<Entity> IShiftEntityCreateAsync<Entity, ViewAndUpsertDTO>.CreateAsync(ViewAndUpsertDTO dto, long? userId)
    {
        return UpsertAsync(new Entity(), dto, ActionTypes.Insert, userId);
    }

    ValueTask<Entity> IShiftEntityUpdateAsync<Entity, ViewAndUpsertDTO>.UpdateAsync(Entity entity, ViewAndUpsertDTO dto, long? userId)
    {
        return UpsertAsync(entity, dto, ActionTypes.Update, userId);
    }

    /// <summary>
    /// CreateAsync and UpdateAsync both of them higher priority
    /// </summary>
    /// <param name="entity"></param>
    /// <param name="dto"></param>
    /// <param name="actionType"></param>
    /// <param name="userId"></param>
    /// <returns></returns>
    /// <exception cref="NotImplementedException"></exception>
    public ValueTask<Entity> UpsertAsync(Entity entity, ViewAndUpsertDTO dto, ActionTypes actionType, long? userId = null)
    {
        throw new NotImplementedException();
    }
}
