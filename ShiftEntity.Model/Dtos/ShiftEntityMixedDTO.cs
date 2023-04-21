namespace ShiftSoftware.ShiftEntity.Model.Dtos;

public abstract class ShiftEntityMixedDTO : ShiftEntityDTO
{
    public ICollection<RevisionDTO>? Revisions { get; set; }
}
