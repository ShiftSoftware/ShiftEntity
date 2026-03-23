using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
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
    /// </summary>
    public static long ToForeignKey(this ShiftEntitySelectDTO selectDTO)
    {
        return long.Parse(selectDTO.Value);
    }

    /// <summary>
    /// Parses a nullable FK value from a ShiftEntitySelectDTO.
    /// Returns null when the DTO is null or its Value is empty.
    /// Usage: existing.CountryOfOriginID = dto.CountryOfOrigin.ToNullableForeignKey();
    /// </summary>
    public static long? ToNullableForeignKey(this ShiftEntitySelectDTO? selectDTO)
    {
        if (selectDTO is null || string.IsNullOrWhiteSpace(selectDTO.Value))
            return null;

        return long.Parse(selectDTO.Value);
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
}
