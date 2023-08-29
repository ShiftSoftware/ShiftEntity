using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.ShiftEntity.Model.HashId;
using System.Linq;

namespace ShiftSoftware.ShiftEntity.Web.Services;

public static class ShiftEntityHashIds
{
    public static long Decode<T>(string key)
    {
        //This is actually redundant, The same logic exists in hashId.Decode.
        //But this is here for performance and to avoid the overhead of creating a new hashId object.
        if (!HashId.Enabled)
            return long.Parse(key);

        var hashId = typeof(T).GetProperty(nameof(ShiftEntityDTOBase.ID))?.GetCustomAttributes(typeof(JsonHashIdConverterAttribute), true)
                .Cast<JsonHashIdConverterAttribute>()
                .FirstOrDefault()?.Hashids;

        if (hashId == null)
            return long.Parse(key);

        return hashId.Decode(key);
    }

    public static string Encode<T>(long id)
    {
        //This is actually redundant, The same logic exists in hashId.Decode.
        //But this is here for performance and to avoid the overhead of creating a new hashId object.
        if (!HashId.Enabled)
            return id.ToString();

        var hashId = typeof(T).GetProperty(nameof(ShiftEntityDTOBase.ID))?.GetCustomAttributes(typeof(JsonHashIdConverterAttribute), true)
                .Cast<JsonHashIdConverterAttribute>()
                .FirstOrDefault()?.Hashids;

        if (hashId == null)
            return id.ToString();

        return hashId.Encode(id);
    }
}
