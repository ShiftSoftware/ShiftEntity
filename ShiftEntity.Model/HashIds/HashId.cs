using HashidsNet;
using System;

namespace ShiftSoftware.ShiftEntity.Model.HashIds;
public static class HashId
{
    internal static bool acceptUnencodedIds;
    internal static bool Enabled;

    internal static bool IdentityHashIdEnabled = false;
    internal static string IdentityHashIdSalt = "";
    internal static int IdentityHashIdMinLength;
    internal static string? IdentityHashIdAlphabet;

    [Obsolete("Configure HashId via x.HashId.RegisterHashId(...) inside AddShiftEntityWeb and inject IHashIdService. The static path is kept for backward compatibility.")]
    public static void RegisterHashId(bool acceptUnencodedIds = false)
    {
        HashId.acceptUnencodedIds = acceptUnencodedIds;

        HashId.Enabled = true;
    }

    [Obsolete("Configure HashId via x.HashId.RegisterIdentityHashId(...) inside AddShiftEntityWeb and inject IHashIdService. The static path is kept for backward compatibility.")]
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

    public ShiftEntityHashId(string salt, int minHashLength = 0, string? alphabet = null)
    {
        if (alphabet == null)
            hashids = new Hashids(salt, minHashLength);
        else
            hashids = new Hashids(salt, minHashLength, alphabet);
    }

    // Encode/Decode are unconditional. Whether to invoke the hasher at all is decided upstream:
    //  - DI path: HashIdConverterRuntime.IsEnabled gates on IHashIdService.IsConfigurationRegistered.
    //  - Legacy static path: JsonHashIdConverterAttribute only materializes a hasher when
    //    HashId.Enabled / HashId.IdentityHashIdEnabled is set at construction time.
    public long Decode(string hash)
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

    public string Encode(long id) => hashids.EncodeLong(id);
}
