using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ShiftSoftware.ShiftEntity.Core.Tagging;
using ShiftSoftware.ShiftEntity.Model.Dtos.Tagging;

namespace ShiftSoftware.ShiftEntity.EFCore.Tagging;

/// <summary>
/// Resolves the DTO-supplied tag list against the existing vocabulary and attaches the
/// matches to the entity's Tags navigation. Called from
/// <see cref="ShiftRepository{DB, EntityType, ListDTO, ViewAndUpsertDTO}.UpsertAsync"/>
/// when both the entity and DTO opt into tagging.
///
/// Only EXISTING tags are attached — submitted tags with no matching row are ignored.
/// New tags are created explicitly through the tag form/endpoints (e.g. ShiftTagPicker's
/// "+" add button), never implicitly on save.
/// </summary>
internal static class TaggingPipeline
{
    internal static async ValueTask ApplyTagsAsync(
        ShiftDbContext db,
        IShiftEntityTaggable entity,
        List<TagDTO>? dtoTags)
    {
        // Resolve options from the application service provider (NOT db.GetService<T>(), whose
        // internal EF SP doesn't carry our IOptions registration). See ShiftDbContext.
        var options = db.ApplicationServiceProvider?.GetService<IOptions<ShiftTaggingOptions>>()?.Value;
        if (options is null || !options.Enabled)
        {
            // Tagging not registered for this DbContext — ignore tag mutations silently.
            return;
        }

        var normalized = NormalizeAndDedupe(dtoTags ?? new List<TagDTO>());

        entity.Tags.Clear();
        if (normalized.Count == 0)
            return;

        var (idsToResolve, namesToResolve) = SplitByIdentity(normalized);

        var tagSet = db.Set<Tag>();
        var matched = new Dictionary<long, Tag>();

        if (idsToResolve.Count > 0)
        {
            var byId = await tagSet
                .Where(t => idsToResolve.Contains(t.ID) && !t.IsDeleted)
                .ToListAsync();
            foreach (var t in byId) matched[t.ID] = t;
        }

        if (namesToResolve.Count > 0)
        {
            var byName = await tagSet
                .Where(t => namesToResolve.Contains(t.Name) && !t.IsDeleted)
                .ToListAsync();
            foreach (var t in byName) matched[t.ID] = t;
        }

        foreach (var t in matched.Values)
            entity.Tags.Add(t);
    }

    private static List<TagDTO> NormalizeAndDedupe(List<TagDTO> input)
    {
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<TagDTO>(input.Count);

        foreach (var t in input)
        {
            if (t is null) continue;
            var name = t.Name?.Trim();

            if (!string.IsNullOrEmpty(t.ID))
            {
                if (seenIds.Add(t.ID!))
                    result.Add(t);
            }
            else if (!string.IsNullOrWhiteSpace(name))
            {
                t.Name = name!;
                if (seenNames.Add(name!))
                    result.Add(t);
            }
        }

        return result;
    }

    private static (List<long> ids, List<string> names) SplitByIdentity(List<TagDTO> tags)
    {
        var ids = new List<long>();
        var names = new List<string>();

        foreach (var t in tags)
        {
            if (!string.IsNullOrEmpty(t.ID) && long.TryParse(t.ID, out var parsed) && parsed != 0)
                ids.Add(parsed);
            else if (!string.IsNullOrWhiteSpace(t.Name))
                names.Add(t.Name);
        }

        return (ids, names);
    }
}
