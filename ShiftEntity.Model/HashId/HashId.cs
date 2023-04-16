using HashidsNet;

namespace ShiftSoftware.ShiftEntity.Model.HashId;
public static class HashId
{
    internal static Hashids? hashids;
    internal static bool acceptUnencodedIds;

    public static void RegisterHashId(bool acceptUnencodedIds = false, string? salt = null, int minHashLength = 0, string? alphabet = null)
    {
        HashId.acceptUnencodedIds = acceptUnencodedIds;

        if (alphabet == null)
            hashids = new Hashids(salt, minHashLength);
        else
            hashids = new Hashids(salt, minHashLength, alphabet);
    }
    public static string Encode(long id)
    {
        if (hashids == null)
            return id.ToString();

        return hashids.EncodeLong(id);
    }
    public static long Decode(string hash)
    {
        if (hashids == null)
            return long.Parse(hash);

        try
        {
            return hashids.DecodeSingleLong(hash);
        }
        catch
        {
            if (acceptUnencodedIds)
            {
                try
                {
                    return long.Parse(hash);
                }
                catch
                {
                }
            }

            return default;
        }
    }
}
