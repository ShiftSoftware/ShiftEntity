using HashidsNet;

namespace ShiftSoftware.ShiftEntity.Web.Services;
public static class HashIdService
{
    private static Hashids hashids;
    public static void RegisterHashId(string salt = null, int minHashLength = 0, string alphabet = null)
    {
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

        return hashids.DecodeSingleLong(hash);
    }
}
