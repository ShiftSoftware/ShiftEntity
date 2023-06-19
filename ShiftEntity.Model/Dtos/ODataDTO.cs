using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Model.Dtos;

public class ODataDTO<TValue>
{
    [JsonPropertyName("@odata.count")]
    public long? Count { get; set; }
    public List<TValue> Value { get; set; } = default!;
}
