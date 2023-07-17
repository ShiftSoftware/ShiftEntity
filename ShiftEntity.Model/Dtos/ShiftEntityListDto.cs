namespace ShiftSoftware.ShiftEntity.Model.Dtos;

public abstract class ShiftEntityListDTO : ShiftEntityDTOBase
{

    public bool IsDeleted { get; set; }
    public List<RevisionDTO> Revisions { get; set; } = new List<RevisionDTO>();
}
