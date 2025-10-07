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
}
