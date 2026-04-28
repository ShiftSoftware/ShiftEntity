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
}
