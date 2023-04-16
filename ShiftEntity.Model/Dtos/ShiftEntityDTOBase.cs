using ShiftSoftware.ShiftEntity.Model.HashId;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ShiftSoftware.ShiftEntity.Model.Dtos;

public class ShiftEntityDTOBase
{
    [Key]
    [JsonConverter(typeof(JsonHashIdConverter))]
    public string? ID { get; set; }
}
