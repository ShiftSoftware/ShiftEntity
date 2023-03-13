using System;

namespace ShiftSoftware.ShiftEntity.Core.Dtos
{
    public class RevisionDTO : ShiftEntityDTOBase
    {
        public DateTime? ValidFrom { get; set; }
        public DateTime? ValidTo { get; set; }
        public long? SavedByUserID { get; set; }
    }
}
