using ShiftSoftware.ShiftEntity.Core;

namespace ShiftSoftware.ShiftEntity.EFCore;

public interface IShiftRepositoryWithOptions<EntityType, ListDTO, ViewAndUpsertDTO> where EntityType : ShiftEntity<EntityType>, new()
{
    public ShiftRepositoryOptions<EntityType, ListDTO, ViewAndUpsertDTO> ShiftRepositoryOptions { get; set; }
}