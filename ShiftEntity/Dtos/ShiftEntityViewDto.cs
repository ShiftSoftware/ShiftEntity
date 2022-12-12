using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace ShiftSoftware.ShiftEntity.Core.Dtos;

public class ShiftEntityViewDto
{
    public Guid ID { get; set; }

    [DataType(DataType.DateTime)]
    public DateTime CreateDate { get; set; }

    [DataType(DataType.DateTime)]
    public DateTime LastSaveDate { get; set; }

    public Guid? CreatedByUserID { get; set; }

    public Guid? LastSavedByUserID { get; set; }

    public bool IsDeleted { get; set; }
}
