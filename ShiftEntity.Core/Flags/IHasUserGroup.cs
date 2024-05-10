namespace ShiftSoftware.ShiftEntity.Core.Flags;

public interface IEntityHasTeam<Entity>
    where Entity : ShiftEntityBase, new()
{
    long? TeamID { get; set; }
}