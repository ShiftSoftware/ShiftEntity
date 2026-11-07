using ShiftSoftware.ShiftEntity.Model.HashIds;
using ShiftSoftware.ShiftEntity.Model.Notifications;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ShiftSoftware.ShiftEntity.Core.Notifications;

/// <summary>One reason a subscribers entry is refused, and the part of the entry it is about.</summary>
public sealed record NotificationSubscribersEntryError(string Field, string Message);

/// <summary>
/// The rules a subscribers entry must pass. They need the host's topics and its hash settings. An entry that breaks them
/// is dropped when the list loads, and a host's test can prove that its list has none.
/// </summary>
public sealed class NotificationSubscribersEntryValidator
{
    private readonly NotificationTopicCatalog catalog;
    private readonly IHashIdService hashIds;

    public NotificationSubscribersEntryValidator(NotificationTopicCatalog catalog, IHashIdService hashIds)
    {
        this.catalog = catalog;
        this.hashIds = hashIds;
    }

    /// <summary>The reasons this entry is refused. An empty list means it is valid.</summary>
    /// <param name="entry">The entry to check.</param>
    /// <param name="otherEntries">
    /// The list's other entries for the same topic, to refuse a second entry with the same <c>where</c>.
    /// </param>
    public IReadOnlyList<NotificationSubscribersEntryError> Validate(
        NotificationSubscribersEntryDTO entry,
        IEnumerable<NotificationSubscribersEntryDTO>? otherEntries = null)
    {
        var errors = new List<NotificationSubscribersEntryError>();

        void Refuse(string field, string message) => errors.Add(new(field, message));

        ValidateTopic(entry, Refuse);
        ValidateWhere(entry, Refuse);
        ValidateIDs(entry.Users, NotificationIdentityHashIds.User, nameof(entry.Users), "user", Refuse);
        ValidateIDs(entry.Teams, NotificationIdentityHashIds.Team, nameof(entry.Teams), "team", Refuse);
        ValidateAddresses(entry, Refuse);
        ValidateTags(entry, Refuse);

        var hasAddresses = entry.Emails?.HasAny == true || entry.BranchEmails?.HasAny == true;

        if (!hasAddresses && entry.Users.Count == 0 && entry.Teams.Count == 0)
            Refuse(nameof(entry.Users), "The entry has no users, teams, emails or branch emails.");

        var key = entry.Where?.GetComparisonKey() ?? NotificationWhere.AllText;

        if (otherEntries?.Any(other => other.Topic == entry.Topic && (other.Where?.GetComparisonKey() ?? NotificationWhere.AllText) == key) == true)
            Refuse(nameof(entry.Where), "This topic already has an entry with the same where. Add the recipients to that entry.");

        return errors;
    }

    private void ValidateTopic(NotificationSubscribersEntryDTO entry, Action<string, string> refuse)
    {
        if (!catalog.TryGetTopic(entry.Topic, out var topic))
        {
            refuse(nameof(entry.Topic), $"'{entry.Topic}' is not a topic of this system.");
            return;
        }

        if (topic.ForCustomers)
            refuse(nameof(entry.Topic), $"'{topic.ID}' is a topic for customers, so it can't have entries. Its emails go only to the customer on the record.");

        var hasAddresses = entry.Emails?.HasAny == true || entry.BranchEmails?.HasAny == true;

        if (hasAddresses && !topic.AllowEmails && !topic.ForCustomers)
            refuse(nameof(entry.Emails), $"'{topic.ID}' doesn't allow email addresses, so this entry can only have users and teams.");
    }

    private void ValidateWhere(NotificationSubscribersEntryDTO entry, Action<string, string> refuse)
    {
        var where = entry.Where;

        if (where is null)
        {
            if (entry.Emails?.HasAny == true || entry.BranchEmails?.HasAny == true)
                refuse(nameof(entry.Where), "An entry with emails or branch emails must have a where. Use \"all\" for every record.");

            return;
        }

        foreach (var part in where.UnreadableParts)
            refuse(nameof(entry.Where), $"The where has {part}. Use \"all\", or an object with any of company, branch, brand, region, city, country and team.");

        if (where.UnreadableParts.Count > 0)
            return;

        if (where.IsAll && where.HasKeys)
            refuse(nameof(entry.Where), "A where is either \"all\" or a list of keys, not both.");

        if (!where.IsAll && !where.HasKeys)
            refuse(nameof(entry.Where), "The where is empty. Use \"all\" for every record.");

        ValidateKey(where.Company, NotificationIdentityHashIds.Company, "company", refuse);
        ValidateKey(where.Branch, NotificationIdentityHashIds.Branch, "branch", refuse);
        ValidateKey(where.Brand, NotificationIdentityHashIds.Brand, "brand", refuse);
        ValidateKey(where.Region, NotificationIdentityHashIds.Region, "region", refuse);
        ValidateKey(where.City, NotificationIdentityHashIds.City, "city", refuse);
        ValidateKey(where.Country, NotificationIdentityHashIds.Country, "country", refuse);
        ValidateKey(where.Team, NotificationIdentityHashIds.Team, "team", refuse);
    }

    private void ValidateKey(string? hash, JsonHashIdConverterAttribute kind, string key, Action<string, string> refuse)
    {
        if (hash is not null && !hashIds.TryDecode(hash, kind, out _))
            refuse(nameof(NotificationSubscribersEntryDTO.Where), $"'{hash}' is not a valid hash ID for {key}.");
    }

    private void ValidateIDs(List<string> ids, JsonHashIdConverterAttribute kind, string field, string name, Action<string, string> refuse)
    {
        foreach (var id in ids)
            if (!hashIds.TryDecode(id, kind, out _))
                refuse(field, $"'{id}' is not a valid hash ID for a {name}.");
    }

    private static void ValidateAddresses(NotificationSubscribersEntryDTO entry, Action<string, string> refuse)
    {
        if (entry.Emails is not { } emails)
            return;

        foreach (var address in emails.To.Concat(emails.Cc).Concat(emails.Bcc))
            if (!NotificationEmailAddress.IsPlainAddress(address))
                refuse(nameof(entry.Emails), $"'{address}' is not a valid email address.");
    }

    private static void ValidateTags(NotificationSubscribersEntryDTO entry, Action<string, string> refuse)
    {
        if (entry.BranchEmails is not { } tags)
            return;

        if (tags.To.Concat(tags.Cc).Concat(tags.Bcc).Any(string.IsNullOrWhiteSpace))
            refuse(nameof(entry.BranchEmails), "A branch email tag can't be empty.");
    }
}
