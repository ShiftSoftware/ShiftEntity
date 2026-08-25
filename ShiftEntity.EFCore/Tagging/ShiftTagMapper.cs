using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Tagging;
using ShiftSoftware.ShiftEntity.Model.Dtos.Tagging;
using System.Linq;

namespace ShiftSoftware.ShiftEntity.EFCore.Tagging;

/// <summary>
/// The framework's own mapper for the tag-vocabulary triple (<see cref="Tag"/>, <see cref="TagListDTO"/>,
/// <see cref="TagDTO"/>).
/// <para>
/// Hand-written on purpose, and it has to be. The source generator is attached as an analyzer only to
/// <c>ShiftEntity.Core</c> with <c>PrivateAssets="all"</c>, so nothing generates a mapper for a triple whose
/// repository lives in <c>ShiftEntity.EFCore</c> — and <see cref="ShiftTagRepository{DB}"/> is framework-owned,
/// so a consumer has no seam to supply one either. Without this class, Tag CRUD would have ridden on
/// <c>ShiftTaggingAutoMapperProfile</c> and 500d in every consumer that called <c>AddShiftTagging</c> the day
/// the AutoMapper fallback was removed, with no consumer-side workaround — which is why it was written first.
/// </para>
/// <para>
/// Registered by <c>AddShiftTagging</c>. <see cref="ShiftTagRepository{DB}"/> already has a constructor taking
/// this interface, and the built-in repository resolves it from DI anyway, so no repository change is needed.
/// Stateless, so the scoped registration is a convention rather than a requirement.
/// </para>
/// </summary>
public sealed class ShiftTagMapper : IShiftEntityMapper<Tag, TagListDTO, TagDTO>
{
    /// <summary>
    /// Reuses <see cref="TagProjection"/> — the same expression spliced into taggable list queries — so the tag
    /// shape has exactly one definition, then chains <c>MapBaseFields</c> for the audit columns and
    /// <c>IsDeleted</c>, which the projection deliberately omits because it exists to fill a tag chip where they
    /// would be noise. (<c>ID</c> is set by both; the second assignment is the same value.)
    /// </summary>
    public TagDTO MapToView(Tag entity, MappingContext context = default)
        => TagProjection.ToDtoSingle(entity).MapBaseFields(entity);

    /// <summary>
    /// The four domain columns, plus the audit and soft-delete columns — matching what generated mappers write
    /// after the Q7 decision (2026-08-22) and what the AutoMapper profile this replaces always wrote. The mapper
    /// maps; restricting who may change a value is not its concern. Use <c>IgnoreEntity</c> or a repository-side
    /// guard if a particular entity needs one.
    /// <para>
    /// <c>ID</c> is not written, matching <c>EntityExcludedMembers</c>: it is the key, it arrives null on every
    /// insert, and the repository has already established it by loading <c>existing</c>.
    /// </para>
    /// <para>
    /// The replication bookkeeping columns (<c>LastReplicationDate</c>, <c>LastReplicationStamp</c>) have no DTO
    /// counterpart and are written only by the replication pipeline, so they are untouched here.
    /// </para>
    /// </summary>
    public Tag MapToEntity(TagDTO dto, Tag existing, MappingContext context = default)
    {
        existing.Name = dto.Name;
        existing.Color = dto.Color;
        existing.Description = dto.Description;
        existing.IntegrationID = dto.IntegrationID;

        existing.IsDeleted = dto.IsDeleted;
        existing.CreateDate = dto.CreateDate;
        existing.LastSaveDate = dto.LastSaveDate;
        existing.CreatedByUserID = MappingHelpers.ToNullableLong(dto.CreatedByUserID);
        existing.LastSavedByUserID = MappingHelpers.ToNullableLong(dto.LastSavedByUserID);

        return existing;
    }

    /// <summary>
    /// An EF-translatable member-init projection.
    /// <para>
    /// <c>IsDeleted</c> is bound EXPLICITLY and is not optional: the Web layer appends
    /// <c>.Where(x =&gt; !x.IsDeleted)</c> to the already-projected DTO queryable, and on EF Core an unbound
    /// member makes that predicate untranslatable — the endpoint 500s rather than leaking rows, but it 500s on
    /// every request. <c>ID</c> is bound for the same reason: <c>$orderby</c> and <c>$filter</c> run against the
    /// projected DTO. <c>MapBaseListFields</c> cannot help here on two counts — <see cref="TagListDTO"/> derives
    /// from <c>ShiftEntityDTOBase</c>, not <c>ShiftEntityListDTO</c>, and it is an in-memory call that no
    /// projection can translate.
    /// </para>
    /// </summary>
    public IQueryable<TagListDTO> MapToList(IQueryable<Tag> query, MappingContext context = default)
        => query.Select(t => new TagListDTO
        {
            ID = t.ID.ToString(),
            IsDeleted = t.IsDeleted,
            Name = t.Name,
            Color = t.Color,
            IntegrationID = t.IntegrationID,
        });

    /// <summary>
    /// <c>ShallowCopyTo</c> is the documented default body — scalars and navigation references, with the key and
    /// the pipeline flags preserved on the target. Reached on <c>ReloadAfterSave</c>. Note this also fixes a
    /// latent hole: no <c>CreateMap&lt;Tag, Tag&gt;</c> ever existed, so the AutoMapper path was a no-op here.
    /// </summary>
    public void CopyEntity(Tag source, Tag target, MappingContext context = default)
        => source.ShallowCopyTo(target);
}
