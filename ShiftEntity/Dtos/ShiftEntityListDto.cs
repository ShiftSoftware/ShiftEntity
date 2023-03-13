using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace ShiftSoftware.ShiftEntity.Core.Dtos
{
    public class ShiftEntityListDTO : ShiftEntityDTOBase
    {
        public List<RevisionDTO> Revisions { get; set; } = new List<RevisionDTO>();
    }
}
