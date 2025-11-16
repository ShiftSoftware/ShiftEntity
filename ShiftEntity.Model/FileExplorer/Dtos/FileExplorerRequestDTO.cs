namespace ShiftSoftware.ShiftEntity.Model.FileExplorer.Dtos;


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

public class FileExplorerSearchDTO : FileExplorerRequestDTOBase
{
    public bool IncludeDeleted { get; set; }
    public string? Query { get; set; }
}

public class FileExplorerMoveDTO : FileExplorerRequestDTOBase
{
    public string[] Paths { get; set; } = [];
}

public class FileExplorerCopyDTO : FileExplorerRequestDTOBase
{
    public string[] Paths { get; set; } = [];
}

public class FileExplorerRenameDTO : FileExplorerRequestDTOBase
{
    public string? NewPath { get; set; }
}
