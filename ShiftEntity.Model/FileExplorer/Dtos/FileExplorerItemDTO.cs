namespace ShiftSoftware.ShiftEntity.Model.FileExplorer.Dtos;

public class FileExplorerItemDTO
{
    public string? Path { get; set; }
    public string? Name { get; set; }
    public bool IsDeleted { get; set; }
    public string? Type { get; set; }
    public bool IsFile { get; set; }
    public long Size { get; set; }
    public DateTime DateModified { get; set; }
    public DateTime CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public string? Url { get; set; }
    public string? ThumbnailUrl { get; set; }
    public object? Additional { get; set; }
}
