using ShiftSoftware.ShiftEntity.Model.HashId;
using System;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ShiftSoftware.ShiftEntity.Model.Dtos;

public class ShiftEntityDTO : ShiftEntityDTOBase
{
    [DataType(DataType.DateTime)]
    public DateTime CreateDate { get; set; }

    [DataType(DataType.DateTime)]
    public DateTime LastSaveDate { get; set; }

    [JsonConverter(typeof(JsonHashIdConverter))]
    public string? CreatedByUserID { get; set; }

    [JsonConverter(typeof(JsonHashIdConverter))]
    public string? LastSavedByUserID { get; set; }

    public bool IsDeleted { get; set; }
}
