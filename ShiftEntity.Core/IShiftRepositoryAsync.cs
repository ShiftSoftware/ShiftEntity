using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Core;

public interface IShiftRepositoryAsync<Entity, ListDTO, DTO> :
    IShiftRepositoryAsync<Entity, ListDTO, DTO, DTO, DTO>
    where Entity : ShiftEntity<Entity>, new()
    where ListDTO : ShiftEntityDTOBase
{
    ValueTask<Entity> IShiftEntityCreateAsync<Entity, DTO>.CreateAsync(DTO dto, long? userId)
    {
        return UpsertAsync(new Entity(), dto, ActionTypes.Insert, userId);
    }

    ValueTask<Entity> IShiftEntityUpdateAsync<Entity, DTO>.UpdateAsync(Entity entity, DTO dto, long? userId)
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
    public ValueTask<Entity> UpsertAsync(Entity entity, DTO dto, ActionTypes actionType, long? userId = null)
    {
        throw new NotImplementedException();
    }
}

public interface IShiftRepositoryAsync<Entity, ListDTO, SelectDTO, CreateDTO, UpdateDTO> :
    IShiftOdataList<Entity, ListDTO>,
    IShiftEntityFind<Entity>,
    IShiftEntityPrepareForReplicationAsync<Entity>,
    IShiftEntityViewAsync<Entity, SelectDTO>,
    IShiftEntityCreateAsync<Entity, CreateDTO>,
    IShiftEntityUpdateAsync<Entity, UpdateDTO>,
    IShiftEntityDeleteAsync<Entity>
    where Entity : ShiftEntity<Entity>
    where ListDTO : ShiftEntityDTOBase
{
    void Add(Entity entity);
    Task SaveChangesAsync(bool raiseBeforeCommitTriggers = false);

    Message ResponseMessage { get; set; }
    Dictionary<string, object> AdditionalResponseData { get; set; }
}
