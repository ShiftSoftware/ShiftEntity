using System.ComponentModel.DataAnnotations;
using System;
using System.Collections.Generic;

namespace ShiftSoftware.ShiftEntity.Core.Dtos
{
    public class ShiftEntityMixedDTO: ShiftEntityDTO
    {
        public ICollection<RevisionDTO>? Revisions { get; set; }
    }
}
