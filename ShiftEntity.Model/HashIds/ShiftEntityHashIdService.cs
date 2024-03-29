﻿using ShiftSoftware.ShiftEntity.Model.Dtos;

namespace ShiftSoftware.ShiftEntity.Model.HashIds;

public static class ShiftEntityHashIdService
{
    public static long Decode<T>(string key)
    {
        return Decode(key, typeof(T));
    }

    public static string Encode<T>(long id)
    {
        return Encode(id, typeof(T));
    }

    public static long Decode(string key, Type type)
    {
        //This is actually redundant, The same logic exists in hashId.Decode.
        //But this is here for performance and to avoid the overhead of creating a new hashId object.
        if (!HashIds.HashId.Enabled)
            return long.Parse(key);

        var hashId = type.GetProperty(nameof(ShiftEntityDTOBase.ID))?.GetCustomAttributes(typeof(JsonHashIdConverterAttribute), true)
                .Cast<JsonHashIdConverterAttribute>()
                .FirstOrDefault()?.Hashids;

        if (hashId == null)
            return long.Parse(key);

        return hashId.Decode(key);
    }

    public static string Encode(long id, Type type)
    {
        //This is actually redundant, The same logic exists in hashId.Decode.
        //But this is here for performance and to avoid the overhead of creating a new hashId object.
        if (!HashIds.HashId.Enabled)
            return id.ToString();

        var hashId = type.GetProperty(nameof(ShiftEntityDTOBase.ID))?.GetCustomAttributes(typeof(JsonHashIdConverterAttribute), true)
                .Cast<JsonHashIdConverterAttribute>()
                .FirstOrDefault()?.Hashids;

        if (hashId == null)
            return id.ToString();

        return hashId.Encode(id);
    }
}
