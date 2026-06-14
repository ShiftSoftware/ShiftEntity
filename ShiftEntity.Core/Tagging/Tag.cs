using System.ComponentModel.DataAnnotations;

namespace ShiftSoftware.ShiftEntity.Core.Tagging;

public class Tag : ShiftEntity<Tag>
{
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
