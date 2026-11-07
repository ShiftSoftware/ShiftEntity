using System.Text.Json.Serialization;

namespace ShiftSoftware.ShiftEntity.Model.Notifications;

/// <summary>
/// One entry of a subscribers list: who hears about a topic, for which records. A topic repeats in the list only when
/// its <see cref="Where"/> differs. The IDs are hash IDs, as the Identity dashboard shows them, so the same text can be
/// copied from there and between environments that share the Identity hash settings.
/// </summary>
public class NotificationSubscribersEntryDTO
{
    /// <summary>The stable ID of the topic.</summary>
    [JsonPropertyName("topic")]
    public string Topic { get; set; } = "";

    /// <summary>
    /// Which records this entry hears about. An entry with <see cref="Emails"/> or <see cref="BranchEmails"/> must have
    /// one. An entry with only <see cref="Users"/> or <see cref="Teams"/> may leave it out, which means all.
    /// </summary>
    [JsonPropertyName("where")]
    public NotificationWhere? Where { get; set; }

    /// <summary>
    /// The hash IDs of Identity users. Each one still has to pass the access checks. A null from JSON reads as an empty
    /// list.
    /// </summary>
    [JsonPropertyName("users")]
    public List<string> Users { get; set => field = value ?? new(); } = new();

    /// <summary>
    /// The hash IDs of Identity teams. Each member still has to pass the access checks. A null from JSON reads as an
    /// empty list.
    /// </summary>
    [JsonPropertyName("teams")]
    public List<string> Teams { get; set => field = value ?? new(); } = new();

    /// <summary>Plain email addresses, such as shared mailboxes. An address has no user to check.</summary>
    [JsonPropertyName("emails")]
    public NotificationRecipients? Emails { get; set; }

    /// <summary>
    /// Tag names. At send time they become the addresses with that tag on the branch document of the record's own
    /// branch.
    /// </summary>
    [JsonPropertyName("branchEmails")]
    public NotificationRecipients? BranchEmails { get; set; }
}
