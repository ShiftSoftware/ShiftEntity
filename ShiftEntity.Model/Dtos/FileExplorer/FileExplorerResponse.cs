namespace ShiftSoftware.ShiftEntity.Model.Dtos;

public class FileExplorerResponse
{
    public FileExplorerDirectoryContent? CWD { get; set; }
    public IEnumerable<FileExplorerDirectoryContent>? Files { get; set; }
    public ErrorDetails? Error { get; set; }
    public FileDetails? Details { get; set; }
}
