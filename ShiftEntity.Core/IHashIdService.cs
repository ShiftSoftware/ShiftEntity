using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.ShiftEntity.Model.HashIds;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Options;

namespace ShiftSoftware.ShiftEntity.Core;

/// <summary>
/// DI-registered, non-static replacement for the legacy <see cref="HashId"/> / <see cref="ShiftEntityHashIdService"/>
/// statics. Reads its configuration from <see cref="HashIdOptions"/> (populated via the existing
/// <c>x.HashId.RegisterHashId(...)</c> / <c>x.HashId.RegisterIdentityHashId(...)</c> /
/// <c>x.HashId.RegisterHashId(name, ...)</c> fluent API inside <c>AddShiftEntityWeb</c>) and
/// exposes Encode/Decode against a per-DTO hasher cache.
/// </summary>
public interface IHashIdService : IHashIdServiceReader
{
    /// <summary>
    /// Returns the per-configuration <c>AcceptUnencodedIds</c> flag for the given configuration
    /// name (resolves to <c>"Default"</c> when null). Returns false when the configuration isn't
    /// registered.
    /// </summary>
    bool IsAcceptUnencodedIds(string? configurationName);

    long Decode<TDTO>(string key);
    long Decode(string key, Type dtoType);
    long Decode(string key, JsonHashIdConverterAttribute attr);
    string Encode<TDTO>(long id);
    string Encode(long id, Type dtoType);
    string Encode(long id, JsonHashIdConverterAttribute attr);

    /// <summary>
    /// Returns the hasher matching the attribute's effective configuration. Used by the
    /// <c>TypeInfoResolver</c> modifier to build JSON converters with a live hasher at type-info
    /// build time (after DI is fully configured), avoiding the attribute-construction-time timing
    /// race that affected the legacy static path.
    /// </summary>
    ShiftEntityHashId? GetHasherFor(JsonHashIdConverterAttribute attr);
}

public class HashIdService : IHashIdService
{
    private readonly HashIdOptions options;
    private readonly ConcurrentDictionary<string, ShiftEntityHashId> hasherCache = new();
    private readonly ConcurrentDictionary<Type, JsonHashIdConverterAttribute?> dtoIdAttributeCache = new();

    public HashIdService(IOptions<ShiftEntityOptions> options)
    {
        this.options = options.Value.HashId ?? new HashIdOptions();
    }

    public bool IsConfigurationRegistered(string configurationName)
        => this.options.Configurations.ContainsKey(configurationName);

    public bool IsAcceptUnencodedIds(string? configurationName)
    {
        var name = configurationName ?? JsonHashIdConverterAttribute.DefaultConfigurationName;
        return this.options.Configurations.TryGetValue(name, out var cfg) && cfg.AcceptUnencodedIds;
    }

    public ShiftEntityHashId? GetHasherFor(JsonHashIdConverterAttribute attr)
    {
        if (attr is null) return null;

        // Resolution rule: named configuration wins over literal Salt/MinHashLength/Alphabet
        // when both are set on the attribute. If the named entry exists but has no Salt of its
        // own (the entry was seeded by RegisterHashId(bool) without explicit values), the
        // attribute's literal values are used as a fallback so a "Default-only" registration
        // acts purely as an enable gate.
        string? salt;
        int minLen;
        string? alpha;

        if (attr.ConfigurationName is { } name)
        {
            if (!options.Configurations.TryGetValue(name, out var cfg))
                return null;

            if (cfg.Salt is null)
            {
                salt = attr.Salt;
                minLen = attr.MinHashLength;
                alpha = attr.Alphabet;
            }
            else
            {
                salt = cfg.Salt;
                minLen = cfg.MinHashLength;
                alpha = cfg.Alphabet;
            }
        }
        else
        {
            options.Configurations.TryGetValue(JsonHashIdConverterAttribute.DefaultConfigurationName, out var dflt);

            if (dflt is null || dflt.Salt is null)
            {
                salt = attr.Salt;
                minLen = attr.MinHashLength;
                alpha = attr.Alphabet;
            }
            else
            {
                salt = dflt.Salt;
                minLen = dflt.MinHashLength;
                alpha = dflt.Alphabet;
            }
        }

        var typed = attr.DtoType is not null;
        var key = typed
            ? $"typed|{attr.DtoType!.FullName}|{attr.ConfigurationName}|{salt}|{minLen}|{alpha}"
            : $"plain|{attr.ConfigurationName}|{salt}|{minLen}|{alpha}";

        return hasherCache.GetOrAdd(key, _ =>
        {
            if (typed)
            {
                var finalSalt = (salt ?? string.Empty)
                    + new string(attr.DtoType!.FullName!.Reverse().ToArray());
                return new ShiftEntityHashId(finalSalt, minLen, alpha);
            }
            else
            {
                return new ShiftEntityHashId(salt ?? string.Empty, minLen, alpha);
            }
        });
    }

    public long Decode<TDTO>(string key) => Decode(key, typeof(TDTO));

    public long Decode(string key, Type dtoType)
    {
        var attr = GetIdAttribute(dtoType);
        if (attr is null)
            return long.Parse(key);

        return Decode(key, attr);
    }

    public long Decode(string key, JsonHashIdConverterAttribute attr)
    {
        var configName = attr.ConfigurationName ?? JsonHashIdConverterAttribute.DefaultConfigurationName;
        if (!IsConfigurationRegistered(configName))
            return long.Parse(key);

        var hasher = GetHasherFor(attr);
        if (hasher is null)
            return long.Parse(key);

        return hasher.Decode(key);
    }

    public string Encode<TDTO>(long id) => Encode(id, typeof(TDTO));

    public string Encode(long id, Type dtoType)
    {
        var attr = GetIdAttribute(dtoType);
        if (attr is null)
            return id.ToString();

        return Encode(id, attr);
    }

    public string Encode(long id, JsonHashIdConverterAttribute attr)
    {
        var configName = attr.ConfigurationName ?? JsonHashIdConverterAttribute.DefaultConfigurationName;
        if (!IsConfigurationRegistered(configName))
            return id.ToString();

        var hasher = GetHasherFor(attr);
        if (hasher is null)
            return id.ToString();

        return hasher.Encode(id);
    }

    private JsonHashIdConverterAttribute? GetIdAttribute(Type dtoType)
    {
        return dtoIdAttributeCache.GetOrAdd(dtoType, t =>
            t.GetProperty(nameof(ShiftEntityDTOBase.ID))
                ?.GetCustomAttributes(typeof(JsonHashIdConverterAttribute), true)
                .Cast<JsonHashIdConverterAttribute>()
                .FirstOrDefault());
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Static helpers for external use — work without DI / IHashIdService instance.
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Encodes a long ID using an explicit configuration. No attribute or DI lookup.
    /// </summary>
    public static string Encode(long id, string salt, int minHashLength = 0, string? alphabet = null)
        => new ShiftEntityHashId(salt, minHashLength, alphabet).Encode(id);

    /// <summary>
    /// Decodes a hash string using an explicit configuration. No attribute or DI lookup.
    /// </summary>
    public static long Decode(string hash, string salt, int minHashLength = 0, string? alphabet = null)
        => new ShiftEntityHashId(salt, minHashLength, alphabet).Decode(hash);

    /// <summary>
    /// Encodes a long ID using configuration read from <c>[JsonHashIdConverter*]</c> on the DTO's
    /// <c>ID</c> property. Any non-null override parameter wins over the attribute's value.
    /// </summary>
    public static string EncodeFor<TDTO>(long id, string? salt = null, int? minHashLength = null, string? alphabet = null)
        => EncodeViaAttribute(id, GetIdAttributeStatic(typeof(TDTO)), salt, minHashLength, alphabet);

    /// <summary>
    /// Decodes a hash using configuration read from <c>[JsonHashIdConverter*]</c> on the DTO's
    /// <c>ID</c> property. Any non-null override parameter wins over the attribute's value.
    /// </summary>
    public static long DecodeFor<TDTO>(string hash, string? salt = null, int? minHashLength = null, string? alphabet = null)
        => DecodeViaAttribute(hash, GetIdAttributeStatic(typeof(TDTO)), salt, minHashLength, alphabet);

    /// <summary>
    /// Encodes a long ID using configuration read from an attribute type
    /// (e.g. <c>UserHashIdConverter</c>). Any non-null override parameter wins over the attribute's value.
    /// </summary>
    public static string EncodeWith<TAttribute>(long id, string? salt = null, int? minHashLength = null, string? alphabet = null)
        where TAttribute : JsonHashIdConverterAttribute, new()
        => EncodeViaAttribute(id, new TAttribute(), salt, minHashLength, alphabet);

    /// <summary>
    /// Decodes a hash using configuration read from an attribute type
    /// (e.g. <c>UserHashIdConverter</c>). Any non-null override parameter wins over the attribute's value.
    /// </summary>
    public static long DecodeWith<TAttribute>(string hash, string? salt = null, int? minHashLength = null, string? alphabet = null)
        where TAttribute : JsonHashIdConverterAttribute, new()
        => DecodeViaAttribute(hash, new TAttribute(), salt, minHashLength, alphabet);

    private static string EncodeViaAttribute(long id, JsonHashIdConverterAttribute? attr, string? saltOverride, int? minLengthOverride, string? alphabetOverride)
    {
        if (attr is null) return id.ToString();
        var (salt, minLen, alpha) = ResolveAttributeConfig(attr, saltOverride, minLengthOverride, alphabetOverride);
        return new ShiftEntityHashId(salt, minLen, alpha).Encode(id);
    }

    private static long DecodeViaAttribute(string hash, JsonHashIdConverterAttribute? attr, string? saltOverride, int? minLengthOverride, string? alphabetOverride)
    {
        if (attr is null) return long.Parse(hash);
        var (salt, minLen, alpha) = ResolveAttributeConfig(attr, saltOverride, minLengthOverride, alphabetOverride);
        return new ShiftEntityHashId(salt, minLen, alpha).Decode(hash);
    }

    private static (string Salt, int MinHashLength, string? Alphabet) ResolveAttributeConfig(
        JsonHashIdConverterAttribute attr,
        string? saltOverride,
        int? minLengthOverride,
        string? alphabetOverride)
    {
        var salt = saltOverride ?? attr.Salt ?? string.Empty;
        var minLen = minLengthOverride ?? attr.MinHashLength;
        var alpha = alphabetOverride ?? attr.Alphabet;

        // Mirror the runtime behavior: typed attributes (DtoType set) augment the salt with the
        // reversed full name of the DTO/converter type for per-type uniqueness.
        if (attr.DtoType is not null)
            salt += new string(attr.DtoType.FullName!.Reverse().ToArray());

        return (salt, minLen, alpha);
    }

    private static JsonHashIdConverterAttribute? GetIdAttributeStatic(Type dtoType)
        => dtoType.GetProperty(nameof(ShiftEntityDTOBase.ID))
            ?.GetCustomAttributes(typeof(JsonHashIdConverterAttribute), true)
            .Cast<JsonHashIdConverterAttribute>()
            .FirstOrDefault();
}
