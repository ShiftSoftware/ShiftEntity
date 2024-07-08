namespace ShiftSoftware.ShiftEntity.Model.Dtos;

public abstract class ShiftEntityListDTO : ShiftEntityDTOBase
{
    [System.Text.Json.Serialization.JsonIgnore]
    [Newtonsoft.Json.JsonIgnore]
    public List<RevisionDTO> Revisions { get; set; } = new List<RevisionDTO>();
}
