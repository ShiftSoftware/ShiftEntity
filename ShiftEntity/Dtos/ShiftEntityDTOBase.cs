using System.ComponentModel.DataAnnotations;
using System;

namespace ShiftSoftware.ShiftEntity.Core.Dtos
{
    public class ShiftEntityDTOBase
    {
        [Key]
        public long ID { get; set; }
    }
}
