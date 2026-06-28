using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Tagging;
using ShiftSoftware.ShiftEntity.Model.Dtos.Tagging;

namespace ShiftSoftware.ShiftEntity.EFCore.Tagging;

public class ShiftTagRepository<DB> : ShiftRepository<DB, Tag, TagListDTO, TagDTO>
    where DB : ShiftDbContext
{
    public ShiftTagRepository(DB db) : base(db)
    {
    }

    public ShiftTagRepository(DB db, IShiftEntityMapper<Tag, TagListDTO, TagDTO> entityMapper)
        : base(db, o => o.UseMapper(entityMapper))
    {
    }
}
