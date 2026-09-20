using ShiftMapper;
using ShiftSoftware.ShiftEntity.Core.Flags;
using ShiftSoftware.ShiftEntity.Core.Tagging;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace ShiftSoftware.ShiftEntity.Core.Mapping;

/// <summary>
/// The framework's mapping conventions, as ShiftMapper rules — what the repository's generated mapping did by
/// hand-written convention until now, said once here for every map the framework declares and for every map a
/// data project writes itself.
///
/// <para>Nothing here is an attribute on a programmer's type. The rules name the framework's OWN base types and
/// interfaces, so they reach an entity by what it derives from and a DTO by what it derives from, and a data
/// project never has to mention them: the markers on <c>ShiftRepository&lt;,,,&gt;</c> and the endpoint
/// attributes carry this pack as their <c>Rules</c>, which puts it at the furthest level of every map in a
/// project that closes them — nearer rules, a <c>ForMember</c> or a mapper class's own <c>CreateConversion</c>,
/// still win.</para>
///
/// <para>Also shared by <c>RegisterShiftRepositories</c> (<c>ShareConversions</c>), so a host that registers
/// ShiftMapper itself gets it for its own maps too.</para>
/// </summary>
public class ShiftEntityConversions : ShiftMapperConversions
{
    public ShiftEntityConversions()
    {
        // ── Members the framework owns. ──────────────────────────────────────────────────────────────────
        //
        // ID is assigned by the database and never taken from a request — and never copied entity to entity
        // (CopyEntity refreshes a tracked row from a fresh load; its key stays). It is still READ into a DTO.
        IgnoreMember<ShiftEntityBase>(e => e.ID, MemberRole.Destination);

        // Request-scoped flags on the entity: neither read into a DTO nor written from one nor copied.
        IgnoreMember(typeof(ShiftEntity<>), nameof(ShiftEntity<object>.ReloadAfterSave));
        IgnoreMember(typeof(ShiftEntity<>), nameof(ShiftEntity<object>.AuditFieldsAreSet));

        // The idempotency key is the CLIENT's — the UI generates it so a retried POST creates one row, not two —
        // but it arrives in the Idempotency-Key header, not the body: ShiftEntityCrudHandler reads the header and
        // the repository's upsert stamps it onto the entity AFTER mapping. The mapper never writes it, so a DTO
        // member of that name cannot overwrite the header's value. Same exclusion the old generator had.
        IgnoreMember<IEntityHasIdempotencyKey>(e => e.IdempotencyKey, MemberRole.Destination);

        // Tags are attached by TaggingPipeline on write and populated by the repository on read; a mapper
        // writing them onto an entity would fight the pipeline. Reading them into a DTO is untouched.
        IgnoreMember<IShiftEntityTaggable>(e => e.Tags, MemberRole.Destination);

        // Revisions are loaded on demand by the revision endpoints, never projected or written by a map.
        IgnoreMember<ShiftEntityListDTO>(d => d.Revisions, MemberRole.Destination);
        IgnoreMember<ShiftEntityMixedDTO>(d => d.Revisions, MemberRole.Destination);

        // IsDeleted and the audit columns are deliberately NOT ignored: the mapper maps every property it is
        // given and the repository decides which writes it keeps (it restores IsDeleted on update, and the
        // audit sweep stamps the dates) — the Q7 decision of the AutoMapper removal, kept.

        // ── A foreign key and its navigation, as one select. ─────────────────────────────────────────────
        //
        // Read: Brand = { Value = BrandID, Text = Brand.<the member [ShiftEntityKeyAndName] nominates, else Name> };
        // the Text is optional so an entity with a key and no navigation, or a navigation type with neither a
        // nomination nor a Name, still gets its id. A NULLABLE key that is null leaves the select null (not a
        // select with a null Value), as MappingHelpers.ToSelectDTO(long?) always did.
        // Write: BrandID = Parse(dto.Brand.Value) — derived from the same rule, through the string -> long
        // conversion below, so a blank select is a 400 naming the field and not a silent 0. The navigation
        // beside the key is left alone: a related row is not rebuilt from a value and a label.
        // A COLLECTION of selects (List<ShiftEntitySelectDTO> Departments) reads element by element from a
        // navigation collection — the gap the old generator left open. Read direction only.
        CreateMemberConvention<ShiftEntitySelectDTO>()
            .NameFrom<ShiftEntityKeyAndNameAttribute>(nameof(ShiftEntityKeyAndNameAttribute.Text))
            .Fill(d => d.Value, "{Member}ID")
            .FillIfPossible(d => d.Text, "{Member}.{NameOf}")
            .FillIfPossible(d => d.Text, "{Member}.Name")
            .ForEachElement()
                .Fill(d => d.Value, nameof(ShiftEntityBase.ID))
                .FillIfPossible(d => d.Text, "{NameOf}")
                .FillIfPossible(d => d.Text, "Name");

        // ── Files stored as JSON. ────────────────────────────────────────────────────────────────────────
        //
        // No query form on purpose: a database cannot parse JSON into objects, so a LIST DTO carrying files
        // loses its projection and the build says so (SM0030). Blank JSON reads as an empty list; a null list
        // writes null.
        CreateConversion<string?, List<ShiftFileDTO>?>(memory: MappingHelpers.ToShiftFiles);
        CreateConversion<List<ShiftFileDTO>?, string?>(memory: MappingHelpers.ToJsonString);

        // ── Text that must be a number. ──────────────────────────────────────────────────────────────────
        //
        // ShiftMapper's own parser turns blank text into 0 on a required long, which for a foreign key is a
        // row pointing at nothing. The framework's rule — MappingHelpers.ToForeignKey and ToLong — is that
        // blank or non-numeric text arriving from a request is the CLIENT's mistake: a 400 naming the field.
        // The conversion takes the mapping so it can name it; the query form is what a database would do with
        // the same text, and is never reached from a repository (lists read entities, not DTOs).
        CreateConversion<string?, long>(memory: ToRequiredLong, query: text => Convert.ToInt64(text));
    }

    /// <summary>
    /// <c>string → long</c> for a required member: blank is an error naming the request field, not 0. The
    /// field is the SOURCE member of the mapping — <c>Brand</c> for <c>"ProductDTO.Brand.Value -&gt; Product.BrandID"</c>
    /// — since that is the member the client sent. Same exception, same shape, as <see cref="MappingHelpers.ToForeignKey"/>.
    /// </summary>
    internal static long ToRequiredLong(string? text, string mapping)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw MappingHelpers.InvalidForeignKey(SourceMemberOf(mapping), null);

        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            throw MappingHelpers.InvalidForeignKey(SourceMemberOf(mapping), text);

        return value;
    }

    /// <summary>
    /// The member of the source the value was read from: the first step after the source type in a mapping
    /// the generator writes as <c>"SourceType.Member[.Path] -&gt; DestinationType.Member"</c>.
    /// </summary>
    internal static string SourceMemberOf(string mapping)
    {
        var arrow = mapping.IndexOf(" -> ", StringComparison.Ordinal);
        var source = arrow < 0 ? mapping : mapping.Substring(0, arrow);
        var steps = source.Split('.');

        return steps.Length > 1 ? steps[1] : source;
    }
}
