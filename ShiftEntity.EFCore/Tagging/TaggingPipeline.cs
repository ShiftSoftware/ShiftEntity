using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ShiftSoftware.ShiftEntity.Core.Tagging;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Model.Dtos.Tagging;
using ShiftSoftware.TypeAuth.Core;
using System.Net;

namespace ShiftSoftware.ShiftEntity.EFCore.Tagging;

/// <summary>
/// Resolves the DTO-supplied tag list against the vocabulary, creating missing
/// tags when policy permits, and attaches the result to the entity's Tags
/// navigation. Called from <see cref="ShiftRepository{DB, EntityType, ListDTO, ViewAndUpsertDTO}.UpsertAsync"/>
/// when both the entity and DTO opt into tagging.
/// </summary>
internal static class TaggingPipeline
{
    internal static async ValueTask ApplyTagsAsync(
        ShiftDbContext db,
        IShiftEntityTaggable entity,
        List<TagDTO>? dtoTags)
    {
        // Resolve from the application service provider, NOT db.GetService<T>().
        // db.GetService<T>() goes through EF Core's internal SP. For IOptions<T> the
        // internal SP doesn't have our Configure<ShiftTaggingOptions> registration —
        // and IOptions falls back to a default-constructed instance (Enabled=false)
        // rather than returning null, which would cause us to silently skip the
        // pipeline even when AddShiftTagging was wired up correctly.
        var applicationServices = db.ApplicationServiceProvider;
        var options = applicationServices?.GetService<IOptions<ShiftTaggingOptions>>()?.Value;
        if (options is null || !options.Enabled)
        {
            // Tagging not registered for this DbContext — ignore tag mutations silently.
            // (The Tags navigation isn't part of the model in this case.)
            return;
        }

        dtoTags ??= new List<TagDTO>();

        var normalized = NormalizeAndDedupe(dtoTags);
        if (normalized.Count == 0)
        {
            entity.Tags.Clear();
            return;
        }

        var (idsToResolve, namesToResolve) = SplitByIdentity(normalized);

        var tagSet = db.Set<Tag>();

        var resolvedById = idsToResolve.Count == 0
            ? new List<Tag>()
            : await tagSet
                .Where(t => idsToResolve.Contains(t.ID) && !t.IsDeleted)
                .ToListAsync();

        var resolvedByName = namesToResolve.Count == 0
            ? new List<Tag>()
            : await tagSet
                .Where(t => namesToResolve.Contains(t.Name) && !t.IsDeleted)
                .ToListAsync();

        var existingByNameSet = resolvedByName
            .Select(t => t.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unknownNames = namesToResolve
            .Where(n => !existingByNameSet.Contains(n))
            .ToList();

        var createdTags = new List<Tag>();
        if (unknownNames.Count > 0)
        {
            switch (options.UnknownTagPolicy)
            {
                case UnknownTagPolicy.SilentDrop:
                    // Drop unknown names — only existing tags get attached.
                    break;

                case UnknownTagPolicy.Reject:
                    throw new ShiftEntityException(
                        new Message("Unknown tag", $"Tag '{unknownNames[0]}' does not exist."),
                        (int)HttpStatusCode.BadRequest);

                case UnknownTagPolicy.AutoCreateIfAuthorized:
                {
                    var canCreate = false;
                    if (options.Action is not null)
                    {
                        var typeAuth = applicationServices?.GetService<ITypeAuthService>();
                        canCreate = typeAuth?.Can(options.Action, Access.Write) == true;
                    }
                    else
                    {
                        // No action configured ⇒ no permission gate ⇒ allow create.
                        canCreate = true;
                    }

                    if (!canCreate)
                        throw new ShiftEntityException(
                            new Message("Forbidden", $"Cannot create new tag '{unknownNames[0]}'."),
                            (int)HttpStatusCode.Forbidden);

                    foreach (var name in unknownNames)
                    {
                        var newTag = new Tag { Name = name };
                        tagSet.Add(newTag);
                        createdTags.Add(newTag);
                    }
                    break;
                }
            }
        }

        entity.Tags.Clear();
        foreach (var t in resolvedById) entity.Tags.Add(t);
        foreach (var t in resolvedByName) entity.Tags.Add(t);
        foreach (var t in createdTags) entity.Tags.Add(t);
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
            if (string.IsNullOrWhiteSpace(name)) continue;

            t.Name = name;

            if (!string.IsNullOrEmpty(t.ID))
            {
                if (seenIds.Add(t.ID!))
                    result.Add(t);
            }
            else
            {
                if (seenNames.Add(name))
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
            {
                ids.Add(parsed);
                continue;
            }

            names.Add(t.Name);
        }

        return (ids, names);
    }
}
