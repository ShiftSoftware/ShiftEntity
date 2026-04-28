using ShiftSoftware.ShiftEntity.Model.HashIds;

namespace ShiftSoftware.ShiftEntity.Core;

public class HashIdOptions
{
    public HashIdOptions RegisterHashId(bool acceptUnencodedIds)
    {
        HashId.RegisterHashId(acceptUnencodedIds);
        return this;
    }

    public HashIdOptions RegisterIdentityHashId(string salt = "", int minHashLength = 0, string? alphabet = null)
    {
        HashId.IdentityHashIdSalt = salt;
        HashId.IdentityHashIdMinLength = minHashLength;
        HashId.IdentityHashIdAlphabet = alphabet;

        HashId.IdentityHashIdEnabled = true;

        return this;
    }
}
