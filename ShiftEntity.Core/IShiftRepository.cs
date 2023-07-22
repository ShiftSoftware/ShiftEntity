using ShiftSoftware.ShiftEntity.Model;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Core
{
    public interface IShiftRepository<Entity, ListDTO, DTO> : 
        IShiftRepository<Entity, ListDTO, DTO,DTO,DTO>
        where Entity : ShiftEntity<Entity>, new()
    {
        Entity IShiftEntityCreate<Entity, DTO>.Create(DTO dto, long? userId = null)
        {
            return Upsert(new Entity(), dto, ActionTypes.Insert, userId);
        }

        Entity IShiftEntityUpdate<Entity, DTO>.Update(Entity entity, DTO dto, long? userId = null)
        {
            return Upsert(entity, dto, ActionTypes.Update, userId);
        }

        /// <summary>
        /// Create and Update both of them higher priority
        /// </summary>
        /// <param name="entity"></param>
        /// <param name="dto"></param>
        /// <param name="actionType"></param>
        /// <param name="userId"></param>
        /// <returns></returns>
        /// <exception cref="NotImplementedException"></exception>
        public Entity Upsert(Entity entity, DTO dto, ActionTypes actionType, long? userId = null)
        {
            throw new NotImplementedException();
        }
    }

    public interface IShiftRepository<Entity, ListDTO, SelectDTO, CreateDTO, UpdateDTO> :
        IShiftOdataList<ListDTO>,
        IShiftEntityFind<Entity>,
        IShiftEntityView<Entity, SelectDTO>,
        IShiftEntityCreate<Entity, CreateDTO>,
        IShiftEntityUpdate<Entity, UpdateDTO>,
        IShiftEntityDeleteAsync<Entity>
        where Entity : ShiftEntity<Entity>
    {
        void Add(Entity entity);
        Task SaveChangesAsync();

        Message ResponseMessage { get; set; }
        Dictionary<string, object> AdditionalResponseData { get; set; }
    }
}
