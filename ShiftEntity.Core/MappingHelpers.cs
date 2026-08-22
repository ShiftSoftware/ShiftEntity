using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Model.Dtos;

namespace ShiftSoftware.ShiftEntity.Core;

public static class MappingHelpers
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> CopyablePropertiesCache = new();

    private static readonly HashSet<string> ExcludedFromCopy = new()
    {
        nameof(ShiftEntity<object>.ReloadAfterSave),
        nameof(ShiftEntity<object>.AuditFieldsAreSet),
        nameof(ShiftEntityBase.ID),
    };

    /// <summary>
    /// Shallow-copies all settable properties from source to target, except ID, ReloadAfterSave, and AuditFieldsAreSet.
    /// This is the default implementation for CopyEntity — override only if you need custom behavior.
    /// Uses cached reflection (one-time cost per entity type).
    /// </summary>
    public static void ShallowCopyTo<TEntity>(this TEntity source, TEntity target) where TEntity : class
    {
        var properties = CopyablePropertiesCache.GetOrAdd(typeof(TEntity), type =>
            type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.CanWrite && !ExcludedFromCopy.Contains(p.Name))
                .ToArray());

        foreach (var prop in properties)
        {
            prop.SetValue(target, prop.GetValue(source));
        }
    }

    /// <summary>
    /// Maps the common audit fields from a ShiftEntity to this view/upsert DTO.
    /// Usage: new ProductDTO { ... }.MapBaseFields(entity)
    /// </summary>
    public static TViewDTO MapBaseFields<TViewDTO, TEntity>(this TViewDTO dto, TEntity entity)
        where TViewDTO : ShiftEntityViewAndUpsertDTO
        where TEntity : ShiftEntity<TEntity>
    {
        dto.ID = entity.ID.ToString();
        dto.IsDeleted = entity.IsDeleted;
        dto.CreateDate = entity.CreateDate;
        dto.LastSaveDate = entity.LastSaveDate;
        dto.CreatedByUserID = entity.CreatedByUserID?.ToString();
        dto.LastSavedByUserID = entity.LastSavedByUserID?.ToString();
        return dto;
    }

    /// <summary>
    /// Maps the common base fields from a ShiftEntity to this list DTO.
    /// Not usable inside IQueryable projections (LINQ-to-SQL) — only for in-memory mapping.
    /// Usage: new ProductListDTO { ... }.MapBaseListFields(entity)
    /// </summary>
    public static TListDTO MapBaseListFields<TListDTO, TEntity>(this TListDTO dto, TEntity entity)
        where TListDTO : ShiftEntityListDTO
        where TEntity : ShiftEntity<TEntity>
    {
        dto.ID = entity.ID.ToString();
        dto.IsDeleted = entity.IsDeleted;
        return dto;
    }

    /// <summary>
    /// Copies the common audit fields from one ShiftEntity to another.
    /// Call this in CopyEntity, then copy only your domain-specific fields manually.
    /// </summary>
    public static void CopyBaseFields<TEntity>(this TEntity source, TEntity target)
        where TEntity : ShiftEntity<TEntity>
    {
        target.CreateDate = source.CreateDate;
        target.LastSaveDate = source.LastSaveDate;
        target.CreatedByUserID = source.CreatedByUserID;
        target.LastSavedByUserID = source.LastSavedByUserID;
        target.IsDeleted = source.IsDeleted;
        // ReloadAfterSave is intentionally NOT copied
    }

    /// <summary>
    /// Creates a ShiftEntitySelectDTO from a required (non-nullable) FK.
    /// Usage: entity.ProductBrandID.ToSelectDTO(entity.ProductBrand?.Name)
    /// </summary>
    public static ShiftEntitySelectDTO ToSelectDTO(this long id, string? text = null)
    {
        return new ShiftEntitySelectDTO
        {
            Value = id.ToString(),
            Text = text,
        };
    }

    /// <summary>
    /// Creates a ShiftEntitySelectDTO from a nullable FK.
    /// Returns null when the FK is null.
    /// Usage: entity.CountryOfOriginID.ToSelectDTO(entity.CountryOfOrigin?.Name)
    /// </summary>
    public static ShiftEntitySelectDTO? ToSelectDTO(this long? id, string? text = null)
    {
        if (!id.HasValue)
            return null;

        return new ShiftEntitySelectDTO
        {
            Value = id.Value.ToString(),
            Text = text,
        };
    }

    /// <summary>
    /// Parses a required (non-nullable) FK value from a ShiftEntitySelectDTO.
    /// Usage: existing.ProductBrandID = dto.ProductBrand.ToForeignKey();
    /// <para>
    /// A missing, blank or non-numeric select is the CLIENT's mistake, so this throws a
    /// <see cref="ShiftEntityException"/> carrying 400 and naming the member, rather than the bare
    /// <c>long.Parse</c> it used to be — which turned a blank <c>{"Value":""}</c> body into a 500 with a stack
    /// trace and no indication of which field was at fault. The two paths that reach it that way are a payload
    /// that passes validation with a blank Value, and minimal-API endpoints, whose validation filter runs
    /// DataAnnotations only.
    /// </para>
    /// <para>
    /// Never reached from a LIST projection: the generator inlines the select-DTO member-init there instead of
    /// calling this, so no throwing code enters an expression tree.
    /// </para>
    /// </summary>
    public static long ToForeignKey(this ShiftEntitySelectDTO selectDTO,
        [CallerArgumentExpression(nameof(selectDTO))] string? member = null)
    {
        if (selectDTO is null || string.IsNullOrWhiteSpace(selectDTO.Value))
            throw InvalidForeignKey(member, null);

        if (!long.TryParse(selectDTO.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            throw InvalidForeignKey(member, selectDTO.Value);

        return id;
    }

    /// <summary>
    /// Parses a nullable FK value from a ShiftEntitySelectDTO.
    /// Returns null when the DTO is null or its Value is empty — clearing the FK is legitimate here, which is
    /// why blank is not an error on this overload. A non-numeric Value still throws 400, as above.
    /// Usage: existing.CountryOfOriginID = dto.CountryOfOrigin.ToNullableForeignKey();
    /// </summary>
    public static long? ToNullableForeignKey(this ShiftEntitySelectDTO? selectDTO,
        [CallerArgumentExpression(nameof(selectDTO))] string? member = null)
    {
        if (selectDTO is null || string.IsNullOrWhiteSpace(selectDTO.Value))
            return null;

        if (!long.TryParse(selectDTO.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            throw InvalidForeignKey(member, selectDTO.Value);

        return id;
    }

    // ShiftEntityCrudHandler catches ShiftEntityException around the upsert and emits the same
    // "Model Validation Error" shape the MVC ModelState path produces, so a bad FK reads like any other field
    // error instead of a server fault. `For` is what a form binds an inline error to, hence the trimmed member.
    private static ShiftEntityException InvalidForeignKey(string? member, string? value)
    {
        var field = string.IsNullOrWhiteSpace(member)
            ? null
            : member!.Substring(member.LastIndexOf('.') + 1).Trim();

        var title = value is null
            ? $"'{field ?? "A required reference"}' is required."
            : $"'{field ?? "A reference"}' is not a valid selection.";

        return new ShiftEntityException(
            new Message("Model Validation Error", title) { For = field },
            (int)System.Net.HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// Deserializes a JSON string to List&lt;ShiftFileDTO&gt;.
    /// Returns an empty list when the string is null or empty.
    /// Usage: dto.Photos = entity.Photos.ToShiftFiles();
    /// </summary>
    public static List<ShiftFileDTO>? ToShiftFiles(this string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new List<ShiftFileDTO>();

        return JsonSerializer.Deserialize<List<ShiftFileDTO>>(json) ?? new List<ShiftFileDTO>();
    }

    /// <summary>
    /// Serializes List&lt;ShiftFileDTO&gt; to a JSON string.
    /// Returns null when the list is null.
    /// Usage: entity.Photos = dto.Photos.ToJsonString();
    /// </summary>
    public static string? ToJsonString(this List<ShiftFileDTO>? files)
    {
        if (files is null)
            return null;

        return JsonSerializer.Serialize(files);
    }

    // ─────────────────────── inverse scalar conversions (the write direction) ───────────────────────
    //
    // These mirror what the read direction already does: it turns long into string and enum into int, so the
    // write direction has to turn them back. Without them the generated mapper emitted NO assignment at all for
    // such a member — the field read back fine and silently never saved.
    //
    // They THROW on bad input rather than writing a default. A silent 0 in a required foreign key is worse than
    // a loud failure: the row saves, points at the wrong parent, and nothing tells you. AutoMapper threw here
    // too (Convert.ToInt64), so this is the established behaviour, not a new hazard.
    //
    // The member name comes from [CallerArgumentExpression], so the message names the exact property without
    // the generator having to pass it.

    /// <summary>Parses a required <see cref="long"/> from its string form. Throws when the value is not a number.</summary>
    public static long ToLong(string? value, [CallerArgumentExpression(nameof(value))] string? member = null)
    {
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return parsed;

        throw new ShiftEntityMappingException(member, value, typeof(long));
    }

    /// <summary>Parses a nullable <see cref="long"/>. Null or blank means null; anything else must be a number.</summary>
    public static long? ToNullableLong(string? value, [CallerArgumentExpression(nameof(value))] string? member = null)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return parsed;

        throw new ShiftEntityMappingException(member, value, typeof(long?));
    }

    /// <summary>Parses a required <see cref="Guid"/> from its string form.</summary>
    public static Guid ToGuid(string? value, [CallerArgumentExpression(nameof(value))] string? member = null)
    {
        if (Guid.TryParse(value, out var parsed))
            return parsed;

        throw new ShiftEntityMappingException(member, value, typeof(Guid));
    }

    /// <summary>Parses a nullable <see cref="Guid"/>. Null or blank means null.</summary>
    public static Guid? ToNullableGuid(string? value, [CallerArgumentExpression(nameof(value))] string? member = null)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (Guid.TryParse(value, out var parsed))
            return parsed;

        throw new ShiftEntityMappingException(member, value, typeof(Guid?));
    }

    // ─────────────────────── general scalar conversions ───────────────────────
    //
    // A DTO routinely stores a value in a different type from the entity: a number as text for the wire, text
    // that is really a number, a wider numeric type. Before these, only long/Guid/enum were covered — so
    // `long` <-> `string` worked while `int` <-> `string` did not, which is a strange place for the line to sit
    // and left every other pair silently unmapped.
    //
    // Parsing and formatting are INVARIANT, always. A DTO value crosses machines and locales, and a decimal
    // written on one server has to read back the same on another.

    /// <summary>Formats any value as invariant text — the source of a "number stored as text" DTO member.</summary>
    public static string ToInvariantText<T>(T value) where T : struct, IFormattable =>
        value.ToString(null, CultureInfo.InvariantCulture);

    /// <summary>Formats a nullable value as invariant text. Null stays null.</summary>
    public static string? ToInvariantText<T>(T? value) where T : struct, IFormattable =>
        value?.ToString(null, CultureInfo.InvariantCulture);

    /// <summary>bool has no IFormattable — "True"/"False" is its only form.</summary>
    public static string ToInvariantText(bool value) => value.ToString();

    public static string? ToInvariantText(bool? value) => value?.ToString();

    /// <summary>
    /// Parses text into any parsable value type, and THROWS naming the member when it will not parse.
    /// <para>
    /// Deliberately not "parse or default". A member that quietly becomes 0 or 1970-01-01 saves a row that
    /// looks fine and is wrong, and nothing anywhere reports it. A failure the caller can see and fix is
    /// strictly better — and it is what AutoMapper did here too.
    /// </para>
    /// </summary>
    public static T ToValue<T>(string? value, [CallerArgumentExpression(nameof(value))] string? member = null)
        where T : struct, IParsable<T>
    {
        if (T.TryParse(value, CultureInfo.InvariantCulture, out var parsed))
            return parsed;

        throw new ShiftEntityMappingException(member, value, typeof(T));
    }

    /// <summary>Parses optional text. Null or blank means null; anything else must still parse.</summary>
    public static T? ToNullableValue<T>(string? value, [CallerArgumentExpression(nameof(value))] string? member = null)
        where T : struct, IParsable<T>
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (T.TryParse(value, CultureInfo.InvariantCulture, out var parsed))
            return parsed;

        throw new ShiftEntityMappingException(member, value, typeof(T?));
    }

    /// <summary>Parses text into an enum, by name or by numeric value. Case-insensitive.</summary>
    public static TEnum ToEnum<TEnum>(string? value, [CallerArgumentExpression(nameof(value))] string? member = null)
        where TEnum : struct, Enum
    {
        if (Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed))
            return parsed;

        throw new ShiftEntityMappingException(member, value, typeof(TEnum));
    }

    /// <summary>Parses optional enum text. Null or blank means null.</summary>
    public static TEnum? ToNullableEnum<TEnum>(string? value, [CallerArgumentExpression(nameof(value))] string? member = null)
        where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed))
            return parsed;

        throw new ShiftEntityMappingException(member, value, typeof(TEnum?));
    }
}

/// <summary>
/// A value arriving from a DTO could not be converted to the type the entity stores it in — for example the
/// text "abc" for a numeric foreign key.
/// <para>
/// Its own type so callers can tell a bad-input failure apart from a genuine server fault: this one is the
/// client's doing, and belongs on the 400 side of the line rather than the 500 side.
/// </para>
/// </summary>
public sealed class ShiftEntityMappingException : Exception
{
    public ShiftEntityMappingException(string? member, string? value, Type targetType)
        : base($"Cannot map {(member is null ? "value" : "'" + member + "'")} to {targetType.Name}: " +
               $"{(value is null ? "the value was null" : "'" + value + "' is not a valid " + targetType.Name)}.")
    {
        Member = member;
        Value = value;
        TargetType = targetType;
    }

    /// <summary>The source expression that produced the value, e.g. <c>dto.ServiceIntervalGroupID</c>.</summary>
    public string? Member { get; }

    public string? Value { get; }

    public Type TargetType { get; }
}
