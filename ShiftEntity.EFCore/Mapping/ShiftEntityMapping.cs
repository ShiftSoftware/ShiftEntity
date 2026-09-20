using ShiftMapper;
using ShiftSoftware.ShiftEntity.Core;

namespace ShiftSoftware.ShiftEntity.EFCore;

/// <summary>
/// The argument of <see cref="ShiftRepositoryOptions{EntityType, ListDTO, ViewAndUpsertDTO}.Mapping"/>: what a
/// repository says about the automatic maps ShiftMapper declares for its triple. That is ONE thing — how deep
/// the child maps below them are declared:
///
/// <code>
/// o.Mapping(m =&gt; m.Nested(2));   // the lines and their product; nothing below that
/// </code>
///
/// <para>Nothing else is configured here, on purpose. WHAT a member maps from is not the repository's
/// business: a member customization is an ordinary <c>CreateMap</c> in a <see cref="ShiftMapperBase"/> class,
/// which replaces the automatic map for that pair (SM0047, informational) and is the map any service uses for
/// the pair as well. The repository decides how far the automatic declaration reaches; a mapper class decides
/// what a map does.</para>
///
/// <para><see cref="Nested"/> is read at BUILD time by the ShiftMapper generator — so the depth is a constant
/// and the call a plain statement of the lambda (SM0035 otherwise) — and baked into the maps the closing type
/// declares; the framework's default is 10. At run time the surface only records what was asked, on
/// <see cref="ShiftRepositoryOptions{EntityType, ListDTO, ViewAndUpsertDTO}.NestedMappingDepth"/>.</para>
/// </summary>
public sealed class ShiftEntityMapping<EntityType, ListDTO, ViewAndUpsertDTO> : ShiftMapperConfigurationSurface
    where EntityType : ShiftEntity<EntityType>
{
    /// <summary>The depth <see cref="Nested"/> was given, or <see langword="null"/> when the lambda did not call it.</summary>
    public int? Depth { get; private set; }

    /// <summary>
    /// Caps how many levels of class-typed members below the repository's maps get an automatic map of their own
    /// (root = 0; the framework's default is 10; 0 nests nothing). A constant, because the generator bakes it.
    /// </summary>
    public new ShiftEntityMapping<EntityType, ListDTO, ViewAndUpsertDTO> Nested(int depth)
    {
        base.Nested(depth);
        Depth = depth;
        return this;
    }
}
