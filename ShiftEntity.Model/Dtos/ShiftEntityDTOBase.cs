using System.ComponentModel.DataAnnotations;

namespace ShiftSoftware.ShiftEntity.Model.Dtos;

public abstract class ShiftEntityDTOBase
{
    [Key]
    public abstract string? ID { get; set; }

}
