using ShiftMapper;
using ShiftSoftware.ShiftEntity.Core.Tagging;
using ShiftSoftware.ShiftEntity.Model.Dtos.Tagging;

namespace ShiftSoftware.ShiftEntity.EFCore.Tagging;

/// <summary>
/// The framework's OWN pairs, declared once here so every data project's generated mapper carries them: a
/// taggable DTO's <c>List&lt;TagDTO&gt; Tags</c> maps from the entity's <c>ICollection&lt;Tag&gt;</c> as an
/// ordinary nested collection — in memory and inside a list projection — with nothing written in the project
/// and no <c>SelectWithTags</c>.
///
/// <para>Read direction only, on purpose. Writing tags onto an entity is <c>TaggingPipeline</c>'s job, and the
/// rules pack ignores <c>IShiftEntityTaggable.Tags</c> as a destination so no map ever competes with it. The tag
/// endpoints' own repository keeps its hand-written <c>ShiftTagMapper</c>, which <c>AddShiftTagging</c>
/// registers in DI ahead of anything ShiftMapper would resolve.</para>
///
/// <para>A plain mapper class: nothing is generated onto it, nothing injects it. It is included in the
/// generated mapper of this assembly and of every assembly that references it.</para>
/// </summary>
public class ShiftEntityFrameworkMaps : ShiftMapperBase
{
    public ShiftEntityFrameworkMaps()
    {
        CreateMap<Tag, TagDTO>();
        CreateMap<Tag, TagListDTO>();
    }
}
