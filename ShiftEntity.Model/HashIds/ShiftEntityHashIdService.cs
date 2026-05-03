using ShiftSoftware.ShiftEntity.Model.Dtos;
using System;

namespace ShiftSoftware.ShiftEntity.Model.HashIds;

public static class ShiftEntityHashIdService
{
    [Obsolete("Inject IHashIdService (ShiftSoftware.ShiftEntity.Core) and use its instance Decode<T>(key) method instead. The static path is kept for backward compatibility.")]
    public static long Decode<T>(string key)
    {
        return Decode(key, typeof(T));
    }

    [Obsolete("Inject IHashIdService (ShiftSoftware.ShiftEntity.Core) and use its instance Encode<T>(id) method instead. The static path is kept for backward compatibility.")]
    public static string Encode<T>(long id)
    {
        return Encode(id, typeof(T));
    }

    [Obsolete("Inject IHashIdService (ShiftSoftware.ShiftEntity.Core) and use its instance Decode(key, dtoType) method instead. The static path is kept for backward compatibility.")]
    public static long Decode(string key, Type type)
    {
        //This is actually redundant, The same logic exists in hashId.Decode.
        //But this is here for performance and to avoid the overhead of creating a new hashId object.
        if (!HashIds.HashId.Enabled)
            return long.Parse(key);

        var attribute = type.GetProperty(nameof(ShiftEntityDTOBase.ID))?.GetCustomAttributes(typeof(JsonHashIdConverterAttribute), true)
                .Cast<JsonHashIdConverterAttribute>()
                .FirstOrDefault();

        var hashId = attribute?.Hashids;

        if (hashId == null)
            return long.Parse(key);

        return hashId.Decode(key);
    }

    [Obsolete("Inject IHashIdService (ShiftSoftware.ShiftEntity.Core) and use its instance Decode(key, attr) method instead. The static path is kept for backward compatibility.")]
    public static long Decode(string key, JsonHashIdConverterAttribute jsonHashIdConverterAttribute)
    {
        //This is actually redundant, The same logic exists in hashId.Decode.
        //But this is here for performance and to avoid the overhead of creating a new hashId object.
        if (!HashIds.HashId.Enabled)
            return long.Parse(key);

        var hashId = jsonHashIdConverterAttribute?.Hashids;

        if (hashId == null)
            return long.Parse(key);

        return hashId.Decode(key);
    }

    [Obsolete("Inject IHashIdService (ShiftSoftware.ShiftEntity.Core) and use its instance Encode(id, dtoType) method instead. The static path is kept for backward compatibility.")]
    public static string Encode(long id, Type type)
    {
        //This is actually redundant, The same logic exists in hashId.Decode.
        //But this is here for performance and to avoid the overhead of creating a new hashId object.
        if (!HashIds.HashId.Enabled)
            return id.ToString();

        var attribute = type.GetProperty(nameof(ShiftEntityDTOBase.ID))?
            .GetCustomAttributes(typeof(JsonHashIdConverterAttribute), true)
                .Cast<JsonHashIdConverterAttribute>();

        var hashId = attribute.FirstOrDefault()?.Hashids;

        if (hashId == null)
            return id.ToString();

        return hashId.Encode(id);
    }

    [Obsolete("Inject IHashIdService (ShiftSoftware.ShiftEntity.Core) and use its instance Encode(id, attr) method instead. The static path is kept for backward compatibility.")]
    public static string Encode(long id, JsonHashIdConverterAttribute jsonHashIdConverterAttribute)
    {
        //This is actually redundant, The same logic exists in hashId.Decode.
        //But this is here for performance and to avoid the overhead of creating a new hashId object.
        if (!HashIds.HashId.Enabled)
            return id.ToString();

        var hashId = jsonHashIdConverterAttribute.Hashids;

        if (hashId == null)
            return id.ToString();

        return hashId.Encode(id);
    }
}
