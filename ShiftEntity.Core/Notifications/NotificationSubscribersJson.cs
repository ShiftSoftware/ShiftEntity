using ShiftSoftware.ShiftEntity.Model.Notifications;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShiftSoftware.ShiftEntity.Core.Notifications;

/// <summary>What one section of a subscribers file holds.</summary>
/// <param name="Entries">The valid entries.</param>
/// <param name="Errors">One line for each entry that was dropped, or for a file that can't be read.</param>
/// <param name="Found">Whether the file has this section.</param>
public sealed record NotificationSubscribersJsonSection(
    IReadOnlyList<NotificationSubscribersEntryDTO> Entries,
    IReadOnlyList<string> Errors,
    bool Found);

/// <summary>
/// Reads a subscribers file: a JSON object whose keys are section names, normally environment names, each holding an array
/// of entries. A host uses this in a test to prove that its file has no invalid entries.
/// </summary>
public static class NotificationSubscribersJson
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        // People edit this file by hand.
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // A misspelled key is refused. Ignoring it would quietly lose a block of recipients.
    private static readonly JsonSerializerOptions EntryOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>The names of all the sections in the file.</summary>
    public static IReadOnlyList<string> GetSections(Stream json)
    {
        using var document = JsonDocument.Parse(json, DocumentOptions);

        return document.RootElement.EnumerateObject().Select(section => section.Name).ToList();
    }

    /// <summary>
    /// Reads one section, comparing its name ignoring case. Nothing in the file can make this throw: a problem becomes a
    /// line in <see cref="NotificationSubscribersJsonSection.Errors"/>.
    /// </summary>
    public static NotificationSubscribersJsonSection ReadSection(Stream json, string? section, NotificationSubscribersEntryValidator validator)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(validator);

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(json, DocumentOptions);
        }
        catch (JsonException exception)
        {
            return new([], [$"The file isn't valid JSON: {exception.Message}"], false);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return new([], ["The file must be a JSON object with one key per section."], false);

            foreach (var candidate in document.RootElement.EnumerateObject())
                if (string.Equals(candidate.Name, section, StringComparison.OrdinalIgnoreCase))
                    return ReadEntries(candidate, validator);

            return new([], [], false);
        }
    }

    private static NotificationSubscribersJsonSection ReadEntries(JsonProperty section, NotificationSubscribersEntryValidator validator)
    {
        if (section.Value.ValueKind != JsonValueKind.Array)
            return new([], [$"Section '{section.Name}' must be an array of entries."], true);

        var entries = new List<NotificationSubscribersEntryDTO>();
        var errors = new List<string>();
        var index = -1;

        foreach (var element in section.Value.EnumerateArray())
        {
            index++;

            NotificationSubscribersEntryDTO? entry;

            try
            {
                entry = element.Deserialize<NotificationSubscribersEntryDTO>(EntryOptions);
            }
            catch (JsonException exception)
            {
                errors.Add($"Section '{section.Name}', entry {index}: {exception.Message}");
                continue;
            }

            if (entry is null)
            {
                errors.Add($"Section '{section.Name}', entry {index}: it is empty.");
                continue;
            }

            // The entries accepted so far, so that a second entry for the same topic and where is the one dropped.
            var refusals = validator.Validate(entry, entries);

            if (refusals.Count == 0)
                entries.Add(entry);
            else
                errors.AddRange(refusals.Select(refusal => $"Section '{section.Name}', entry {index}, {refusal.Field}: {refusal.Message}"));
        }

        return new(entries, errors, true);
    }
}
