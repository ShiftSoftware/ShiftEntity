using System.Text.Json.Serialization;

namespace ShiftSoftware.ShiftEntity.Model.Dtos;

public class ShiftEntitySelectDTO
{
    public string Value { get; set; } = default!;

    [JsonConverter(typeof(LocalizedTextJsonConverter))]
    public string? Text { get; set; }

    public List<ShiftEntitySelectDTO>? Nested { get; set; }

    [JsonIgnore]
    public object? Data { get; set; }

    public ShiftEntitySelectDTO()
    {
        
    }
}