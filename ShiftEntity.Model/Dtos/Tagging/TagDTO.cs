using System.ComponentModel.DataAnnotations;

namespace ShiftSoftware.ShiftEntity.Model.Dtos.Tagging;

[ShiftSoftware.ShiftEntity.Model.ShiftEntityKeyAndName(nameof(ID), nameof(Name))]
public class TagDTO : ShiftEntityViewAndUpsertDTO
{
    public override string? ID { get; set; }

    [Required]
    [MaxLength(128)]
    public string Name { get; set; } = default!;

    [MaxLength(32)]
    [RegularExpression(@"^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6})$", ErrorMessage = "Color must be a hex code, e.g. #FF0000.")]
    public string? Color
    {
        get;
        // Format on set: a non-empty colour is trimmed and upper-cased (e.g. "#ff0000" → "#FF0000")
        // so it's stored in one canonical form everywhere it's assigned — the Blazor form, API
        // deserialization, and DTO↔entity mapping. Blank/whitespace collapses to null (optional).
        set => field = string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();
    }

    [MaxLength(512)]
    public string? Description { get; set; }

    [MaxLength(128)]
    public string? IntegrationID { get; set; }
}
