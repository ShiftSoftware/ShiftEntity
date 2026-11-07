using System.Text.Json.Serialization;

namespace ShiftSoftware.ShiftEntity.Model.Notifications;

/// <summary>
/// The To, CC and BCC lists of a subscribers entry. Under <c>emails</c> they hold plain addresses. Under
/// <c>branchEmails</c> they hold tag names. A null from JSON reads as an empty list.
/// </summary>
public sealed class NotificationRecipients
{
    [JsonPropertyName("to")]
    public List<string> To { get; set => field = value ?? new(); } = new();

    [JsonPropertyName("cc")]
    public List<string> Cc { get; set => field = value ?? new(); } = new();

    [JsonPropertyName("bcc")]
    public List<string> Bcc { get; set => field = value ?? new(); } = new();

    /// <summary>Whether any of the three lists has a value.</summary>
    [JsonIgnore]
    public bool HasAny => To.Count > 0 || Cc.Count > 0 || Bcc.Count > 0;
}
