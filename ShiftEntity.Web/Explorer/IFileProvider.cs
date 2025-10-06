using ShiftSoftware.ShiftEntity.Model.Dtos;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Web.Explorer;


public interface IFileProvider
{
    public char Delimiter { get; }

    public Task<FileExplorerResponseDTO> GetFiles(string path, bool includeDeleted);
    public Task<FileExplorerResponseDTO> Create(string path);
    public Task<FileExplorerResponseDTO> Rename(string path, string newName);
    public Task<FileExplorerResponseDTO> Move(string[] names, string targetPath);
    public Task<FileExplorerResponseDTO> Copy(string[] names, string targetPath);
    public Task<FileExplorerResponseDTO> Delete(string[] names);
    public Task<FileExplorerResponseDTO> Details(string path);
    public Task<FileExplorerResponseDTO> Search(string searchString, string path);
    public Task<FileExplorerResponseDTO> Restore(string[] names);
    //public Task Upload(string path, byte[] fileData, string fileName);
    //public Task WriteFile(string path, byte[] fileData, string fileName);
}
