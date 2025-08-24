namespace ShiftSoftware.ShiftEntity.Core.Flags;

public interface IEntityHasTeam<Entity>
    
{
    long? TeamID { get; set; }
}