namespace ShiftSoftware.ShiftEntity.Model.FileExplorer.Dtos;

public class FileExplorerResponseDTO(string? path = null)
{
    public bool Success { get; set; }
    public Message? Message { get; set; }
    public string? Path { get; set; } = path;
    public string? ContinuationToken { set; get; }
    public List<FileExplorerItemDTO>? Items { get; set; }
    public object? Additional { get; set; }
}