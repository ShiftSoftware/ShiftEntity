using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.ShiftEntity.Model.HashId;
using System.Linq;

namespace ShiftSoftware.ShiftEntity.Web.Services;

internal static class ShiftEntityHashIds<T>
{
    public static long Decode(string key)
    {
        //This is actually redundant, The same logic exists in hashId.Decode.
        //But this is here for performance and to avoid the overhead of creating a new hashId object.
        if (!HashId.Enabled)
            return long.Parse(key);

        var hashId = typeof(T).GetProperty(nameof(ShiftEntityDTOBase.ID)).GetCustomAttributes(typeof(JsonHashIdConverterAttribute), true)
                .Cast<JsonHashIdConverterAttribute>()
                .FirstOrDefault()?.Hashids ?? new ShiftEntityHashId("", 0, null);

        return hashId.Decode(key);
    }
}
