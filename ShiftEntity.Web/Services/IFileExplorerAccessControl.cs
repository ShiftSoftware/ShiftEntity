using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.ShiftEntity.Model.FileExplorer.Dtos;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Web.Services;

public interface IFileExplorerAccessControl
{
    public Task<List<FileExplorerItemDTO>> FilterWithReadAccessAsync(Azure.Storage.Blobs.BlobContainerClient container, List<FileExplorerItemDTO> details);
    public List<ShiftFileDTO> FilterWithWriteAccess(List<ShiftFileDTO> files);
    public List<string> FilterWithWriteAccess(string[] files);
    public List<string> FilterWithDeleteAccess(string[] data);
}