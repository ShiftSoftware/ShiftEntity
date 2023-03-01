using System;
using System.ComponentModel.DataAnnotations;

namespace ShiftSoftware.ShiftEntity.Core.Dtos;

public class ShiftEntityDTO : ShiftEntityDTOBase
{
    [DataType(DataType.DateTime)]
    public DateTime CreateDate { get; set; }

    [DataType(DataType.DateTime)]
    public DateTime LastSaveDate { get; set; }

    public long? CreatedByUserID { get; set; }

    public long? LastSavedByUserID { get; set; }

    public bool IsDeleted { get; set; }
}
