using System;
using System.ComponentModel.DataAnnotations;

namespace ShiftSoftware.ShiftEntity.Core.Dtos;

public class ShiftEntityDTO
{
    [Key]
    public Guid ID { get; set; }

    [DataType(DataType.DateTime)]
    public DateTime CreateDate { get; set; }

    [DataType(DataType.DateTime)]
    public DateTime LastSaveDate { get; set; }

    public Guid? CreatedByUserID { get; set; }

    public Guid? LastSavedByUserID { get; set; }

    public bool IsDeleted { get; set; }
}
