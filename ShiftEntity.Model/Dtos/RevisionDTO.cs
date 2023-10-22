using ShiftSoftware.ShiftEntity.Model.HashIds;
using System.Text.Json.Serialization;

namespace ShiftSoftware.ShiftEntity.Model.Dtos
{
    public class RevisionDTO: ShiftEntityListDTO
    {
        [UserHashIdConverter]
        [JsonIgnore]
        public override string? ID { get; set; }
        public DateTime? ValidFrom { get; set; }
        public DateTime? ValidTo { get; set; }

        [UserHashIdConverter]
        public string? SavedByUserID { get; set; }
    }
}
