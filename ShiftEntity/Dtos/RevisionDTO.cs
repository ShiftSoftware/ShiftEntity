﻿using System;

namespace ShiftSoftware.ShiftEntity.Core.Dtos
{
    public class RevisionDTO
    {
        public DateTime? ValidFrom { get; set; }
        public DateTime? ValidTo { get; set; }
        public long? SavedByUserID { get; set; }
    }
}
