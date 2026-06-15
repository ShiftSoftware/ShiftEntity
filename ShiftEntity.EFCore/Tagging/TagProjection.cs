using ShiftSoftware.ShiftEntity.Core.Tagging;
using ShiftSoftware.ShiftEntity.Model.Dtos.Tagging;
using System.Collections.Generic;
using System.Linq;

namespace ShiftSoftware.ShiftEntity.EFCore.Tagging;

/// <summary>
/// Fixed <see cref="Tag"/> → <see cref="TagDTO"/> projection used by the repository's
/// read-side auto-mapping. Tag IDs are plain integers (no HashId), and the DTO shape is
/// framework-owned, so this is a simple hand projection — no AutoMapper / IMapper needed.
/// </summary>
internal static class TagProjection
{
    internal static List<TagDTO> ToDtoList(IEnumerable<Tag>? tags)
        => tags is null
            ? new List<TagDTO>()
            : tags.Select(t => new TagDTO
            {
                ID = t.ID.ToString(),
                Name = t.Name,
                Color = t.Color,
                Description = t.Description,
                IntegrationID = t.IntegrationID,
            }).ToList();
}
