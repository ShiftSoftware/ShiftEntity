namespace ShiftSoftware.ShiftEntity.Core.Flags;

public interface IEntityHasRegion<Entity>
    where Entity : ShiftEntityBase, new()
{
    long? RegionID { get; set; }
}
