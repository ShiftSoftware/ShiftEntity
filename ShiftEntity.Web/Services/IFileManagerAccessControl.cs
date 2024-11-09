using Syncfusion.EJ2.FileManager.Base;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Web.Services;

public interface IFileManagerAccessControl
{
    public Task<List<FileManagerDirectoryContent>> FilterWithReadAccessAsync(Azure.Storage.Blobs.BlobContainerClient container, List<FileManagerDirectoryContent> details);
}