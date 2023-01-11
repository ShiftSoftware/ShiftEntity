using System.ComponentModel.DataAnnotations;
using System;
using System.Collections.Generic;

namespace ShiftSoftware.ShiftEntity.Core.Dtos
{
    public class ShiftEntityMixedDTO: ShiftEntityDTOBase
    {
        [DataType(DataType.DateTime)]
        public DateTime CreateDate { get; set; }

        [DataType(DataType.DateTime)]
        public DateTime LastSaveDate { get; set; }

        public Guid? CreatedByUserID { get; set; }

        public Guid? LastSavedByUserID { get; set; }

        public bool IsDeleted { get; set; }

        public ICollection<RevisionDTO>? Revisions { get; set; }
    }
}
