
using ShiftSoftware.ShiftEntity.Model.Dtos;

namespace ShiftSoftware.ShiftEntity.Core.Flags;

public interface IEntityHasBrand<Entity>
    
{
    long? BrandID { get; set; }
}

public interface IHasBrandSelection<ViewAndUpsertDTO>
    where ViewAndUpsertDTO : ShiftEntityViewAndUpsertDTO, new()
{
    ShiftEntitySelectDTO? Brand { get; set; }
}

public interface IHasBrandForeignColumn<ListDTO>
    where ListDTO : ShiftEntityDTOBase, new()
{
    string? BrandID { get; set; }
}