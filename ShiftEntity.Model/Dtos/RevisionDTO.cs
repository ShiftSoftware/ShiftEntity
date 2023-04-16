using ShiftSoftware.ShiftEntity.Model.HashId;
using System;
using System.Text.Json.Serialization;

namespace ShiftSoftware.ShiftEntity.Model.Dtos
{
    public class RevisionDTO: ShiftEntityListDTO
    {
        public DateTime? ValidFrom { get; set; }
        public DateTime? ValidTo { get; set; }

        [JsonConverter(typeof(JsonHashIdConverter))]
        public string? SavedByUserID { get; set; }
    }
}
