using ShiftSoftware.ShiftEntity.Model.Dtos;
using Syncfusion.EJ2.FileManager.Base;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Web.Services;

public interface IFileExplorerAccessControl
{
    public Task<List<FileManagerDirectoryContent>> FilterWithReadAccessAsync(Azure.Storage.Blobs.BlobContainerClient container, List<FileManagerDirectoryContent> details);
    public List<ShiftFileDTO> FilterWithWriteAccess(List<ShiftFileDTO> files);
    public List<FileManagerDirectoryContent> FilterWithDeleteAccess(FileManagerDirectoryContent[] data);
}