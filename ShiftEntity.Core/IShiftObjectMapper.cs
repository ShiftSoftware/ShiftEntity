using System;

namespace ShiftSoftware.ShiftEntity.Core;

/// <summary>
/// The child-pair mapping contract used by DEEP mapping: maps a child entity type to its DTO type
/// (e.g. <c>InvoiceLine</c> ↔ <c>InvoiceLineDTO</c>). Pair mappers are auto-generated for every
/// (child entity, child DTO) pair the source generator discovers inside view DTOs, and composed
/// automatically into the parents' <c>MapToView</c>. Declare a <c>[ShiftEntityMapper]</c> partial class
/// implementing this interface to customize a pair (it replaces the auto-generated one).
///
/// Distinct from <see cref="IShiftEntityMapper{TEntity, TListDTO, TViewDTO}"/> on purpose: a child has
/// no list DTO of its own, and <c>MapToList</c>/<c>CopyEntity</c> are root-entity concerns.
/// </summary>
public interface IShiftObjectMapper<TEntity, TDto>
{
    /// <summary>Entity → DTO (the view direction; composed automatically into parents' MapToView).</summary>
    TDto Map(TEntity source, IServiceProvider? serviceProvider = null);

    /// <summary>
    /// DTO → entity (the upsert direction). NEVER called automatically — writing children is a
    /// persistence-pattern decision; wire it explicitly (e.g. <c>ForEntityChildren</c>).
    /// </summary>
    TEntity MapBack(TDto dto, TEntity existing, IServiceProvider? serviceProvider = null);
}
