using System.ComponentModel.DataAnnotations;
using System;

namespace ShiftSoftware.ShiftEntity.Core.Dtos
{
    public class ShiftEntityDTOBase
    {
        [Key]
        public Guid ID { get; set; }

        public long SequentialId { get; set; }
    }
}
