using ShiftSoftware.ShiftEntity.Model.HashId;
using System.ComponentModel.DataAnnotations;

namespace ShiftSoftware.ShiftEntity.Model.Dtos;

public abstract class ShiftEntityDTO : ShiftEntityDTOBase
{

    [DataType(DataType.DateTime)]
    public DateTime CreateDate { get; set; }

    [DataType(DataType.DateTime)]
    public DateTime LastSaveDate { get; set; }

    [UserHashIdConverter]
    public string? CreatedByUserID { get; set; }

    [UserHashIdConverter]
    public string? LastSavedByUserID { get; set; }
}
