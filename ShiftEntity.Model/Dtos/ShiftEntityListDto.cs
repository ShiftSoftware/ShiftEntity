using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace ShiftSoftware.ShiftEntity.Model.Dtos
{
    public class ShiftEntityListDTO : ShiftEntityDTOBase
    {
        public List<RevisionDTO> Revisions { get; set; } = new List<RevisionDTO>();
    }
}
