using System;
using System.Collections.Generic;
using System.Text;

namespace ShiftSoftware.ShiftEntity.Model;

public class FileExplorerConfiguration
{
    public string? FunctionsEndpoint { get; set; }
    public string? DatabaseId { get; set; }
    public string? ContainerId { get; set; }
    public int PageSizeHint { get; set; } = 5000;
    public FileExplorerService FileExplorerService { get; set; } = FileExplorerService.None;

    public void UseAzureBlobStorage()
    {
        FileExplorerService = FileExplorerService.AzureBlobStorage;
    }
}

public enum FileExplorerService
{
    None = 0,
    AzureBlobStorage = 1,
}
