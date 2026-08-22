using ShiftSoftware.ShiftEntity.Model.Dtos.Tagging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

namespace ShiftSoftware.ShiftEntity.Core.Tagging;

/// <summary>
/// The single, canonical <see cref="Tag"/> → <see cref="TagDTO"/> projection. Tag IDs are plain
/// integers (no HashId) and the DTO shape is framework-owned, so this is a simple hand projection —
/// no AutoMapper / IMapper needed. It is the one source of truth used by both:
/// <list type="bullet">
///   <item>the read-side view auto-mapping (<see cref="ToDtoList"/>, materialized), and</item>
///   <item>list projections, where <see cref="TaggableProjectionExtensions.SelectWithTags{TEntity,TListDTO}"/>
///   splices <see cref="ToDto"/> inline so EF Core translates it like a hand-written projection.</item>
/// </list>
/// </summary>
public static class TagProjection
{
    /// <summary>Canonical EF-translatable <see cref="Tag"/> → <see cref="TagDTO"/> projection.</summary>
    public static readonly Expression<Func<Tag, TagDTO>> ToDto = t => new TagDTO
    {
        ID = t.ID.ToString(),
        Name = t.Name,
        Color = t.Color,
        Description = t.Description,
        IntegrationID = t.IntegrationID,
    };

    private static readonly Func<Tag, TagDTO> ToDtoFunc = ToDto.Compile();

    /// <summary>
    /// Materialized projection for already-loaded tags (the single-entity read path).
    /// <para>
    /// Deliberately does NOT filter soft-deleted tags. A tag that was attached to an entity stays on that entity
    /// and is returned as-is; retiring a tag from the vocabulary only stops it being attached to anything NEW
    /// (<c>TaggingPipeline</c> resolves live tags only). Soft-delete filtering is the repository and OData
    /// layer's job, not mapping's — see the note on Step B10 in the AutoMapper-removal plan.
    /// </para>
    /// </summary>
    public static List<TagDTO> ToDtoList(IEnumerable<Tag>? tags)
        => tags is null ? new List<TagDTO>() : tags.Select(ToDtoFunc).ToList();

    /// <summary>
    /// Materialized projection of ONE already-loaded tag, so <see cref="ShiftTagMapper.MapToView"/> can reuse
    /// this definition instead of restating the tag shape. The caller chains <c>MapBaseFields</c> for the audit
    /// columns this projection deliberately leaves out.
    /// </summary>
    public static TagDTO ToDtoSingle(Tag tag) => ToDtoFunc(tag);
}
