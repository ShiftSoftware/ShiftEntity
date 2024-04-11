
using ShiftSoftware.ShiftEntity.Model.Dtos;

namespace ShiftSoftware.ShiftEntity.Core.Flags;

public interface IEntityHasDraft<Entity>
    where Entity : ShiftEntityBase, new()
{
    bool IsDraft { get; set; }
}

public interface IHasDraftCheckBox<ViewAndUpsertDTO>
    where ViewAndUpsertDTO : ShiftEntityViewAndUpsertDTO, new()
{
    bool? IsDraft { get; set; }
}

public interface IHasDraftColumn<ListDTO>
    where ListDTO : ShiftEntityDTOBase, new()
{
    bool IsDraft { get; set; }
}