using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace ShiftSoftware.ShiftEntity.Core.Dtos
{
    public class ShiftEntityListDto
    {
        [Key]
        public Guid ID { get; set; }
        public ICollection<RevisionDTO>? Revisions { get; set; }
    }
}
