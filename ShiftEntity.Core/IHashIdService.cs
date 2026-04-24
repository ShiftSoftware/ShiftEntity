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
/// <c>x.HashId.RegisterHashId(...)</c> / <c>x.HashId.RegisterIdentityHashId(...)</c> fluent API inside
/// <c>AddShiftEntityWeb</c>) and exposes Encode/Decode against a per-DTO hasher cache.
/// </summary>
public interface IHashIdService : IHashIdServiceReader
{
    bool AcceptUnencodedIds { get; }

    long Decode<TDTO>(string key);
    long Decode(string key, Type dtoType);
    long Decode(string key, JsonHashIdConverterAttribute attr);
    string Encode<TDTO>(long id);
    string Encode(long id, Type dtoType);
    string Encode(long id, JsonHashIdConverterAttribute attr);

    /// <summary>
    /// Returns the hasher matching the attribute's construction arguments. Used by the
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

    public bool Enabled => this.options.Enabled;
    public bool IdentityHashIdEnabled => this.options.IdentityHashIdEnabled;
    public bool AcceptUnencodedIds => this.options.AcceptUnencodedIds;

    public ShiftEntityHashId? GetHasherFor(JsonHashIdConverterAttribute attr)
    {
        if (attr is null) return null;

        // For identity hashers, substitute salt/minLength/alphabet from HashIdOptions (populated
        // via x.HashId.RegisterIdentityHashId(...) in AddShiftEntityWeb). The attribute's Raw fields
        // are ignored in this case — the identity converter classes (UserHashIdConverter,
        // CountryHashIdConverter, ...) don't read from the static HashId.Identity* fields anymore.
        var effectiveSalt = attr.IsIdentityHasher ? options.IdentityHashIdSalt : attr.RawSalt;
        var effectiveMinLength = attr.IsIdentityHasher ? options.IdentityHashIdMinLength : attr.RawMinHashLength;
        var effectiveAlphabet = attr.IsIdentityHasher ? options.IdentityHashIdAlphabet : attr.RawAlphabet;

        // Build and cache the hasher unconditionally based on the resolved args. The converters
        // that receive this hasher decide at Read/Write time (via the service's Enabled /
        // IdentityHashIdEnabled flags) whether to actually invoke it.
        var key = attr.RawUsedTypedCtor
            ? $"typed|{attr.RawDtoType?.FullName}|{effectiveSalt}|{effectiveMinLength}|{effectiveAlphabet}|{attr.IsIdentityHasher}"
            : $"plain|{effectiveSalt}|{effectiveMinLength}|{effectiveAlphabet}";

        return hasherCache.GetOrAdd(key, _ =>
        {
            if (attr.RawUsedTypedCtor)
            {
                var salt = (effectiveSalt ?? string.Empty)
                    + new string(attr.RawDtoType!.FullName!.Reverse().ToArray());
                return new ShiftEntityHashId(salt, effectiveMinLength, effectiveAlphabet, attr.IsIdentityHasher);
            }
            else
            {
                return new ShiftEntityHashId(effectiveSalt!, effectiveMinLength, effectiveAlphabet);
            }
        });
    }

    public long Decode<TDTO>(string key) => Decode(key, typeof(TDTO));

    public long Decode(string key, Type dtoType)
    {
        if (!Enabled)
            return long.Parse(key);

        var attr = GetIdAttribute(dtoType);
        if (attr is null)
            return long.Parse(key);

        return Decode(key, attr);
    }

    public long Decode(string key, JsonHashIdConverterAttribute attr)
    {
        if (!Enabled && !(IdentityHashIdEnabled && attr.IsIdentityHasher))
            return long.Parse(key);

        var hasher = GetHasherFor(attr);
        if (hasher is null)
            return long.Parse(key);

        return hasher.Decode(key);
    }

    public string Encode<TDTO>(long id) => Encode(id, typeof(TDTO));

    public string Encode(long id, Type dtoType)
    {
        if (!Enabled)
            return id.ToString();

        var attr = GetIdAttribute(dtoType);
        if (attr is null)
            return id.ToString();

        return Encode(id, attr);
    }

    public string Encode(long id, JsonHashIdConverterAttribute attr)
    {
        if (!Enabled && !(IdentityHashIdEnabled && attr.IsIdentityHasher))
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
