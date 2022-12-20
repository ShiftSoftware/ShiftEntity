namespace ShiftSoftware.ShiftEntity.Core
{
    public interface IShiftRepositoryAsync<Entity, ListDTO, DTO> :
        IShiftOdataList<ListDTO>,
        IShiftEntityFind<Entity>,
        IShiftEntityViewAsync<Entity, DTO>,
        IShiftEntityCreateAsync<Entity, DTO>,
        IShiftEntityUpdateAsync<Entity, DTO>,
        IShiftEntityDeleteAsync<Entity>
        where Entity : ShiftEntity<Entity>
    {

    }
}
