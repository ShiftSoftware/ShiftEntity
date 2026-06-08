using HashidsNet;

namespace ShiftSoftware.ShiftEntity.Model.HashIds;

public class ShiftEntityHashId
{
    private readonly Hashids hashids;
    private readonly bool acceptUnencodedIds;

    public ShiftEntityHashId(string salt, int minHashLength = 0, string? alphabet = null, bool acceptUnencodedIds = false)
    {
        if (alphabet == null)
            hashids = new Hashids(salt, minHashLength);
        else
            hashids = new Hashids(salt, minHashLength, alphabet);

        this.acceptUnencodedIds = acceptUnencodedIds;
    }

    public long Decode(string hash)
    {
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

    public string Encode(long id) => hashids.EncodeLong(id);
}
