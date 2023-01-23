using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Core.Dtos;

public class ODataDTO<TValue>
{
    [JsonPropertyName("@odata.count")]
    public Int64? Count { get; set; }
    public TValue Value { get; set; }
}
