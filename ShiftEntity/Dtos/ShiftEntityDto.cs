using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Core.Dtos;

public class ShiftEntityDto
{
    public Guid ID { get; private set; }

    [DataType(DataType.DateTime)]
    public DateTime CreateDate { get; private set; }

    [DataType(DataType.DateTime)]
    public DateTime LastSaveDate { get; private set; }

    public Guid? CreatedByUserID { get; private set; }

    public Guid? LastSavedByUserID { get; private set; }

    public bool IsDeleted { get; private set; }
}
