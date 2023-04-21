using HashidsNet;

namespace ShiftSoftware.ShiftEntity.Model.HashId;
public static class HashId
{
    internal static bool acceptUnencodedIds;
    internal static bool Enabled;

    internal static string UserIdsSalt = "";
    internal static int UserIdsMinHashLength;
    internal static string? UserIdsAlphabet;

    public static void RegisterHashId(bool acceptUnencodedIds = false)
    {
        HashId.acceptUnencodedIds = acceptUnencodedIds;

        HashId.Enabled = true;
    }

    public static void RegisterUserIdsHasher(string salt = "", int minHashLength = 0, string? alphabet = null)
    {
        HashId.UserIdsSalt = salt;
        HashId.UserIdsMinHashLength = minHashLength;
        HashId.UserIdsAlphabet = alphabet;
    }
}

public class ShiftEntityHashId
{
    Hashids hashids;

    public ShiftEntityHashId(string salt, int minHashLength = 0, string? alphabet = null)
    {
        if (alphabet == null)
            hashids = new Hashids(salt, minHashLength);
        else
            hashids = new Hashids(salt, minHashLength, alphabet);
    }

    public long Decode(string hash)
    {
        if (!HashId.Enabled)
            return long.Parse(hash);

        try
        {
            return hashids.DecodeSingleLong(hash);
        }
        catch
        {
            if (HashId.acceptUnencodedIds)
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

    public string Encode(long id)
    {
        if (!HashId.Enabled)
            return id.ToString();

        return hashids.EncodeLong(id);
    }
}