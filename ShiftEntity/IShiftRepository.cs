namespace ShiftSoftware.ShiftEntity.Core
{
    public interface IShiftRepository<Entity, ListDTO, ViewDTO, CrudDTO> :
        IShiftOdataList<ListDTO>,
        IShiftEntityFind<Entity>,
        IShiftEntityView<Entity, ViewDTO>,
        IShiftEntityCreate<Entity, CrudDTO>,
        IShiftEntityUpdate<Entity, CrudDTO>,
        IShiftEntityDelete<Entity>
        where Entity : ShiftEntity<Entity>
    {
    }
}
