using System.ComponentModel.DataAnnotations;

namespace ShiftSoftware.ShiftEntity.Model.Dtos.Tagging;

public class TagDTO : ShiftEntityViewAndUpsertDTO
{
    public override string? ID { get; set; }

    [Required]
    [MaxLength(128)]
    public string Name { get; set; } = default!;

    [MaxLength(32)]
    public string? Color { get; set; }

    [MaxLength(512)]
    public string? Description { get; set; }

    [MaxLength(128)]
    public string? IntegrationID { get; set; }
}
