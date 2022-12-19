namespace ShiftSoftware.ShiftEntity.Core
{
    public interface IShiftRepository<Entity, ListDTO, DTO> :
        IShiftOdataList<ListDTO>,
        IShiftEntityFind<Entity>,
        IShiftEntityView<Entity, DTO>,
        IShiftEntityCreate<Entity, DTO>,
        IShiftEntityUpdate<Entity, DTO>,
        IShiftEntityDelete<Entity>
        where Entity : ShiftEntity<Entity>
    {
    }
}
