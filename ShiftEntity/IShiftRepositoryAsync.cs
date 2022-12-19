namespace ShiftSoftware.ShiftEntity.Core
{
    public interface IShiftRepositoryAsync<Entity, ListDTO, ViewDTO, CrudDTO> :
        IShiftOdataList<ListDTO>,
        IShiftEntityFind<Entity>,
        IShiftEntityViewAsync<Entity, ViewDTO>,
        IShiftEntityCreateAsync<Entity, CrudDTO>,
        IShiftEntityUpdateAsync<Entity, CrudDTO>,
        IShiftEntityDeleteAsync<Entity>
        where Entity : ShiftEntity<Entity>
    {

    }
}
