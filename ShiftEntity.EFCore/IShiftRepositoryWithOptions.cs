using ShiftSoftware.ShiftEntity.Core;

namespace ShiftSoftware.ShiftEntity.EFCore;

public interface IShiftRepositoryWithOptions<EntityType> where EntityType : ShiftEntity<EntityType>, new()
{
    public ShiftRepositoryOptions<EntityType> ShiftRepositoryOptions { get; set; }
}