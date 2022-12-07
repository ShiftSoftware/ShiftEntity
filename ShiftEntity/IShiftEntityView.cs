namespace ShiftSoftware.ShiftEntity.Core
{
    public interface IShiftEntityView<EntityType, ViewDTOType>
        where EntityType: class
    {
        public ViewDTOType View();
    }
}
