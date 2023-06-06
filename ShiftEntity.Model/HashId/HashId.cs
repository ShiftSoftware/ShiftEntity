using HashidsNet;

namespace ShiftSoftware.ShiftEntity.Model.HashId;
public static class HashId
{
    internal static bool acceptUnencodedIds;
    internal static bool Enabled;

    internal static bool UserIdsHashEnabled = false;
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

        HashId.UserIdsHashEnabled = true;
    }
}

public class ShiftEntityHashId
{
    Hashids hashids;
    internal bool UserIdsHasher = false;

    public ShiftEntityHashId(string salt, int minHashLength = 0, string? alphabet = null)
    {
        if (alphabet == null)
            hashids = new Hashids(salt, minHashLength);
        else
            hashids = new Hashids(salt, minHashLength, alphabet);
    }

    internal ShiftEntityHashId(string salt, int minHashLength = 0, string? alphabet = null, bool userIdsHasher = true)
        : this(salt, minHashLength, alphabet)
    {
        this.UserIdsHasher = userIdsHasher;
    }

    public long Decode(string hash)
    {
        if ((HashId.Enabled && !this.UserIdsHasher) || (HashId.UserIdsHashEnabled && this.UserIdsHasher))
        {
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
        else
        {
            return long.Parse(hash);
        }
    }

    public string Encode(long id)
    {
        if ((HashId.Enabled && !this.UserIdsHasher) || (HashId.UserIdsHashEnabled && this.UserIdsHasher))
        {
            return hashids.EncodeLong(id);
        }
        else
        {
            return id.ToString();
        }
    }
}