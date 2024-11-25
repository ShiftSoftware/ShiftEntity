using ShiftSoftware.ShiftEntity.Model.Dtos;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Web.Services;

public interface IFileExplorerAccessControl
{
    public Task<List<FileExplorerDirectoryContent>> FilterWithReadAccessAsync(Azure.Storage.Blobs.BlobContainerClient container, List<FileExplorerDirectoryContent> details);
    public List<ShiftFileDTO> FilterWithWriteAccess(List<ShiftFileDTO> files);
    public List<FileExplorerDirectoryContent> FilterWithDeleteAccess(FileExplorerDirectoryContent[] data);
}