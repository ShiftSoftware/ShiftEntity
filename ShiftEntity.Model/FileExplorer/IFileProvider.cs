using ShiftSoftware.ShiftEntity.Model.FileExplorer.Dtos;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Web.Explorer;


public interface IFileProvider
{
    public Task<FileExplorerResponseDTO> GetFiles(FileExplorerReadDTO data);
    public Task<FileExplorerResponseDTO> Create(FileExplorerCreateDTO data);
    public Task<FileExplorerResponseDTO> Rename(string path, string newName);
    public Task<FileExplorerResponseDTO> Move(string[] names, string targetPath);
    public Task<FileExplorerResponseDTO> Copy(string[] names, string targetPath);
    public Task<FileExplorerResponseDTO> Delete(FileExplorerDeleteDTO data);
    public Task<FileExplorerResponseDTO> Detail(FileExplorerDetailDTO data);
    public Task<FileExplorerResponseDTO> Search(string searchString, string path);
    public Task<FileExplorerResponseDTO> Restore(FileExplorerRestoreDTO data);
    //public Task Upload(string path, byte[] fileData, string fileName);
    //public Task WriteFile(string path, byte[] fileData, string fileName);
}
