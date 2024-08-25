using System.Text.Json.Serialization;

namespace ShiftSoftware.ShiftEntity.Model.Dtos;

public class ODataDTO<TValue>
{
    public long? Count { get; set; }
    public List<TValue> Value { get; set; } = default!;
}
