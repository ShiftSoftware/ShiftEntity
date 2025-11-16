using ShiftSoftware.ShiftEntity.Model.FileExplorer.Dtos;

namespace ShiftSoftware.ShiftEntity.Web.Explorer;

public interface IFileProvider
{
    public Task<FileExplorerResponseDTO> GetFiles(FileExplorerReadDTO data);
    public Task<FileExplorerResponseDTO> Create(FileExplorerCreateDTO data);
    public Task<FileExplorerResponseDTO> Rename(FileExplorerRenameDTO data);
    public Task<FileExplorerResponseDTO> Move(FileExplorerMoveDTO data);
    public Task<FileExplorerResponseDTO> Copy(FileExplorerCopyDTO data);
    public Task<FileExplorerResponseDTO> Delete(FileExplorerDeleteDTO data);
    public Task<FileExplorerResponseDTO> Detail(FileExplorerDetailDTO data);
    public Task<FileExplorerResponseDTO> Search(FileExplorerSearchDTO data);
    public Task<FileExplorerResponseDTO> Restore(FileExplorerRestoreDTO data);
}
