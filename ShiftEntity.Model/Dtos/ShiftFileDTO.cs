using System.Text.Json.Serialization;

namespace ShiftSoftware.ShiftEntity.Model.Dtos;

///////
/////// Heads Up: There's a custom JSON converter for this class in ShiftSoftware.ShiftEntity.Core.Services.Make sure you to check the converter when you're updating this
//////
public class ShiftFileDTO
{
    public string? Name { get; set; }
    public string? AccountName { get; set; } = default!;
    public string? ContainerName { get; set; } = default!;
    public string? Blob { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Url { get; set; }
    public string? ContentType { get; set; }
    public long Size { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
}
