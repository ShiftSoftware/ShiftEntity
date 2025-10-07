
namespace ShiftSoftware.ShiftEntity.Model.Dtos;


public class FileExplorerRequestDTOBase
{
    public string? Path { get; set; }
    public string? AccountName { get; set; }
    public string? ContainerName { get; set; }
}

public class FileExplorerReadDTO : FileExplorerRequestDTOBase
{
    public bool IncludeDeleted { get; set; }
    public string? ContinuationToken { get; set; }
}

public class FileExplorerCreateDTO : FileExplorerRequestDTOBase
{

}

public class  FileExplorerDeleteDTO : FileExplorerRequestDTOBase
{
    public string[] Paths { get; set; } = [];
}

public class FileExplorerRestoreDTO : FileExplorerRequestDTOBase
{
    public string[] Paths { get; set; } = [];
}

public class FileExplorerDetailDTO : FileExplorerReadDTO
{
}

public class FileExplorerResponseDTO(string? path = null)
{
    public bool Success { get; set; }
    public Message? Message { get; set; }
    public string? Path { get; set; } = path;
    public string? ContinuationToken { set; get; }
    public List<FileExplorerItemDTO>? Items { get; set; }
    public object? Additional { get; set; }
}

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
}

//public enum FileExplorerAction
//{
//    Read = 1,
//    Create,
//    Rename,
//    Move,
//    Copy,
//    Delete,
//    Details,
//    Search,
//    Restore,
//}
