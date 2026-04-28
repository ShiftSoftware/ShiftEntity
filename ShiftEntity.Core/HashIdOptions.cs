using ShiftSoftware.ShiftEntity.Model.HashIds;

namespace ShiftSoftware.ShiftEntity.Core;

public class HashIdOptions
{
    public bool Enabled { get; internal set; }
    public bool AcceptUnencodedIds { get; internal set; }

    public bool IdentityHashIdEnabled { get; internal set; }
    public string IdentityHashIdSalt { get; internal set; } = "";
    public int IdentityHashIdMinLength { get; internal set; }
    public string? IdentityHashIdAlphabet { get; internal set; }

    public HashIdOptions RegisterHashId(bool acceptUnencodedIds)
    {
        this.Enabled = true;
        this.AcceptUnencodedIds = acceptUnencodedIds;

#pragma warning disable CS0618
        HashId.RegisterHashId(acceptUnencodedIds);
#pragma warning restore CS0618

        return this;
    }

    public HashIdOptions RegisterIdentityHashId(string salt = "", int minHashLength = 0, string? alphabet = null)
    {
        this.IdentityHashIdEnabled = true;
        this.IdentityHashIdSalt = salt;
        this.IdentityHashIdMinLength = minHashLength;
        this.IdentityHashIdAlphabet = alphabet;

#pragma warning disable CS0618
        HashId.RegisterUserIdsHasher(salt, minHashLength, alphabet);
#pragma warning restore CS0618

        return this;
    }
}
