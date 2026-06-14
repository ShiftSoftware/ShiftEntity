namespace ShiftSoftware.ShiftEntity.Model.Dtos.Tagging;

[ShiftSoftware.ShiftEntity.Model.ShiftEntityKeyAndName(nameof(ID), nameof(Name))]
public class TagListDTO : ShiftEntityDTOBase
{
    public override string? ID { get; set; }

    public string Name { get; set; } = default!;

    public string? Color { get; set; }

    public string? IntegrationID { get; set; }
}
