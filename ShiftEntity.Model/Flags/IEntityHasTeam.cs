namespace ShiftSoftware.ShiftEntity.Model.Flags;

public interface IEntityHasTeam<Entity>
    
{
    long? TeamID { get; set; }
}