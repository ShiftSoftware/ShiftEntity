using System.ComponentModel.DataAnnotations;

namespace ShiftSoftware.ShiftEntity.Functions.FileManager;

public class ZipMessages
{
    [Required]
    public string ContainerName { get; set; } = string.Empty;
    [Required(AllowEmptyStrings = false)]
    public string Path { get; set; } = string.Empty;
    public List<string> FileNames { get; set; } = [];
}
