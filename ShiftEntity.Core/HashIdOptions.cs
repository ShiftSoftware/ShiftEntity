using System;
using System.Collections.Generic;
using ShiftSoftware.ShiftEntity.Model.HashIds;

namespace ShiftSoftware.ShiftEntity.Core;

public sealed record HashIdConfiguration(string? Salt, int MinHashLength, string? Alphabet, bool AcceptUnencodedIds = false);

public class HashIdOptions
{
    public IDictionary<string, HashIdConfiguration> Configurations { get; }
        = new Dictionary<string, HashIdConfiguration>(StringComparer.Ordinal);

    public HashIdOptions RegisterHashId(bool acceptUnencodedIds)
    {
        // Default registration with no explicit salt: enables unnamed attributes via the
        // service.IsConfigurationRegistered("Default") gate while letting them keep using the
        // salt/min/alphabet supplied to the attribute constructor. AcceptUnencodedIds is stored
        // on the Default config and is also propagated to the legacy static for back-compat
        // with external projects that still read HashId.acceptUnencodedIds directly.
#pragma warning disable CS0618
        HashId.RegisterHashId(acceptUnencodedIds);
#pragma warning restore CS0618

        this.Configurations[JsonHashIdConverterAttribute.DefaultConfigurationName]
            = new HashIdConfiguration(Salt: null, MinHashLength: 0, Alphabet: null, AcceptUnencodedIds: acceptUnencodedIds);

        return this;
    }

    public HashIdOptions RegisterHashId(string name, string salt, int minHashLength = 0, string? alphabet = null, bool acceptUnencodedIds = false)
    {
        this.Configurations[name] = new HashIdConfiguration(salt, minHashLength, alphabet, acceptUnencodedIds);
        return this;
    }

    public HashIdOptions RegisterIdentityHashId(string salt = "", int minHashLength = 0, string? alphabet = null, bool acceptUnencodedIds = false)
    {
        this.Configurations[JsonHashIdConverterAttribute.IdentityConfigurationName]
            = new HashIdConfiguration(salt, minHashLength, alphabet, acceptUnencodedIds);

#pragma warning disable CS0618
        HashId.RegisterUserIdsHasher(salt, minHashLength, alphabet);
#pragma warning restore CS0618

        return this;
    }
}
