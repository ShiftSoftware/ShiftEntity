using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Core
{
    public interface IShiftRepositoryAsync<Entity, ListDTO, DTO> :
        IShiftRepositoryAsync<Entity, ListDTO, DTO, DTO, DTO>
        where Entity : ShiftEntity<Entity>
    {
    }

    public interface IShiftRepositoryAsync<Entity, ListDTO, SelectDTO, CreateDTO, UpdateDTO> :
        IShiftOdataList<ListDTO>,
        IShiftEntityFind<Entity>,
        IShiftEntityViewAsync<Entity, SelectDTO>,
        IShiftEntityCreateAsync<Entity, CreateDTO>,
        IShiftEntityUpdateAsync<Entity, UpdateDTO>,
        IShiftEntityDeleteAsync<Entity>
        where Entity : ShiftEntity<Entity>
    {
        void Add(Entity entity);
        Task SaveChangesAsync();
    }
}
