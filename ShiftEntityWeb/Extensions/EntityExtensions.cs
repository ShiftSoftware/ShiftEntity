using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.ShiftEntity.Web.Services;

namespace ShiftSoftware.ShiftEntity.Core;
public static class EntityExtensions
{
    public static string GetHashId<T>(this ShiftEntity<T> shiftEntity) where T : class
    {
        return HashIdService.Encode(shiftEntity.ID);
    }

    public static long GetLongId<T>(this ShiftEntityDTOBase dto) where T : class
    {
        return HashIdService.Decode(dto.ID);
    }
}
