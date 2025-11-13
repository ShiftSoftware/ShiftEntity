using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.ShiftEntity.Model.FileExplorer.Dtos;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Web.Services;

public interface IFileExplorerAccessControl
{
    public Task<IEnumerable<string>> FilterWithReadAccessAsync(Azure.Storage.Blobs.BlobContainerClient container, IEnumerable<string> files);
    public IEnumerable<string> FilterWithWriteAccess(IEnumerable<string> files);
    public IEnumerable<string> FilterWithDeleteAccess(IEnumerable<string> files);
}