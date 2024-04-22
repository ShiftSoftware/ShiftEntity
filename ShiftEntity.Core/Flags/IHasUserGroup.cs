namespace ShiftSoftware.ShiftEntity.Core.Flags;

public interface IEntityHasUserGroup<Entity>
    where Entity : ShiftEntityBase, new()
{
    long? UserGroupID { get; set; }
}