using System.ComponentModel.DataAnnotations;
namespace ShiftSoftware.ShiftEntity.Model.Dtos;

public class ShiftEntityDTOBase
{
    [Key]
    public string? ID { get; set; }
}
