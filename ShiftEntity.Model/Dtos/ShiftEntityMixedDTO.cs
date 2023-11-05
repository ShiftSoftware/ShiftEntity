namespace ShiftSoftware.ShiftEntity.Model.Dtos;

public abstract class ShiftEntityMixedDTO : ShiftEntityViewAndUpsertDTO
{
    public ICollection<RevisionDTO>? Revisions { get; set; }
}
