using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
    /// Maps the common audit fields from a ShiftEntity to a view/upsert DTO.
    /// Call this in MapToView after creating the DTO, to avoid repeating audit field assignments.
    /// </summary>
    public static TViewDTO MapBaseFieldsToView<TEntity, TViewDTO>(this TEntity entity, TViewDTO dto)
        where TEntity : ShiftEntity<TEntity>
        where TViewDTO : ShiftEntityViewAndUpsertDTO
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
    /// Maps the common base fields from a ShiftEntity to a list DTO.
    /// Not usable inside IQueryable projections (LINQ-to-SQL) — only for in-memory mapping.
    /// For IQueryable, assign these fields directly in the Select expression.
    /// </summary>
    public static TListDTO MapBaseFieldsToList<TEntity, TListDTO>(this TEntity entity, TListDTO dto)
        where TEntity : ShiftEntity<TEntity>
        where TListDTO : ShiftEntityListDTO
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
    /// Creates a ShiftEntitySelectDTO from a required (non-nullable) FK and optional navigation name.
    /// Usage: entity.ToSelectDTO(e.ProductBrandID, e.ProductBrand?.Name)
    /// </summary>
    public static ShiftEntitySelectDTO ToSelectDTO(long id, string? text = null)
    {
        return new ShiftEntitySelectDTO
        {
            Value = id.ToString(),
            Text = text,
        };
    }

    /// <summary>
    /// Creates a ShiftEntitySelectDTO from a nullable FK and optional navigation name.
    /// Returns null when the FK is null.
    /// Usage: entity.ToSelectDTO(e.CountryOfOriginID, e.CountryOfOrigin?.Name)
    /// </summary>
    public static ShiftEntitySelectDTO? ToSelectDTO(long? id, string? text = null)
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
}
