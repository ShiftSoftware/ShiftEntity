using ShiftSoftware.ShiftEntity.Model;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Core
{
    public interface IShiftRepository<Entity, ListDTO, DTO> : 
        IShiftRepository<Entity, ListDTO, DTO,DTO,DTO>
        where Entity : ShiftEntity<Entity>
    {
    }

    public interface IShiftRepository<Entity, ListDTO, SelectDTO, CreateDTO, UpdateDTO> :
        IShiftOdataList<ListDTO>,
        IShiftEntityFind<Entity>,
        IShiftEntityView<Entity, SelectDTO>,
        IShiftEntityCreate<Entity, CreateDTO>,
        IShiftEntityUpdate<Entity, UpdateDTO>,
        IShiftEntityDelete<Entity>
        where Entity : ShiftEntity<Entity>
    {
        void Add(Entity entity);
        Task SaveChangesAsync();

        Message ResponseMessage { get; set; }
        Dictionary<string, object> AdditionalResponseData { get; set; }
    }
}
