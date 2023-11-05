using ShiftSoftware.ShiftEntity.Model.HashIds;
using System.ComponentModel.DataAnnotations;

namespace ShiftSoftware.ShiftEntity.Model.Dtos;

public abstract class ShiftEntityViewAndUpsertDTO : ShiftEntityDTOBase
{

    [DataType(DataType.DateTime)]
    public DateTimeOffset CreateDate { get; set; }

    [DataType(DataType.DateTime)]
    public DateTimeOffset LastSaveDate { get; set; }

    [UserHashIdConverter]
    public string? CreatedByUserID { get; set; }

    [UserHashIdConverter]
    public string? LastSavedByUserID { get; set; }
}
