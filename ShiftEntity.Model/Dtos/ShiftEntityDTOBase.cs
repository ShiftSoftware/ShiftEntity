using System.ComponentModel.DataAnnotations;
using System;

namespace ShiftSoftware.ShiftEntity.Model.Dtos
{
    public class ShiftEntityDTOBase
    {
        [Key]
        public long ID { get; set; }
    }
}
