using HashidsNet;

namespace ShiftSoftware.ShiftEntity.Model.HashId;
public static class HashId
{
    internal static bool acceptUnencodedIds;
    internal static bool Enabled;

    internal static bool IdentityHashIdEnabled = false;
    internal static string IdentityHashIdSalt = "";
    internal static int IdentityHashIdMinLength;
    internal static string? IdentityHashIdAlphabet;

    public static void RegisterHashId(bool acceptUnencodedIds = false)
    {
        HashId.acceptUnencodedIds = acceptUnencodedIds;

        HashId.Enabled = true;
    }

    public static void RegisterUserIdsHasher(string salt = "", int minHashLength = 0, string? alphabet = null)
    {
        HashId.IdentityHashIdSalt = salt;
        HashId.IdentityHashIdMinLength = minHashLength;
        HashId.IdentityHashIdAlphabet = alphabet;

        HashId.IdentityHashIdEnabled = true;
    }
}

public class ShiftEntityHashId
{
    Hashids hashids;
    internal bool IsIdentityHasher = false;

    public ShiftEntityHashId(string salt, int minHashLength = 0, string? alphabet = null)
    {
        if (alphabet == null)
            hashids = new Hashids(salt, minHashLength);
        else
            hashids = new Hashids(salt, minHashLength, alphabet);
    }

    internal ShiftEntityHashId(string salt, int minHashLength = 0, string? alphabet = null, bool userIdsHasher = false)
        : this(salt, minHashLength, alphabet)
    {
        this.IsIdentityHasher = userIdsHasher;
    }

    public long Decode(string hash)
    {
        if ((HashId.Enabled && !this.IsIdentityHasher) || (HashId.IdentityHashIdEnabled && this.IsIdentityHasher))
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
        if ((HashId.Enabled && !this.IsIdentityHasher) || (HashId.IdentityHashIdEnabled && this.IsIdentityHasher))
        {
            return hashids.EncodeLong(id);
        }
        else
        {
            return id.ToString();
        }
    }
}