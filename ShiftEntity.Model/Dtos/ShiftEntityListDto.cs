namespace ShiftSoftware.ShiftEntity.Model.Dtos;

public abstract class ShiftEntityListDTO : ShiftEntityDTOBase
{
    public List<RevisionDTO> Revisions { get; set; } = new List<RevisionDTO>();
}
