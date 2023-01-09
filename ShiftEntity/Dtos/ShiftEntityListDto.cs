using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace ShiftSoftware.ShiftEntity.Core.Dtos
{
    public class ShiftEntityListDTO : ShiftEntityDTOBase
    {
        public ICollection<RevisionDTO>? Revisions { get; set; }
    }
}
