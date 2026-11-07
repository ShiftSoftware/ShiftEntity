using System.Net.Mail;

namespace ShiftSoftware.ShiftEntity.Core.Notifications;

/// <summary>What counts as an email address on a subscribers list and in the send path.</summary>
public static class NotificationEmailAddress
{
    /// <summary>
    /// Whether the text, ignoring spaces around it, is one plain address. <c>Name &lt;a@b.example&gt;</c> and
    /// <c>a@b.example, c@d.example</c> aren't, even though some of them read as a valid <see cref="MailAddress"/>.
    /// </summary>
    public static bool IsPlainAddress(string? address)
    {
        var trimmed = address?.Trim();

        return !string.IsNullOrEmpty(trimmed)
            && MailAddress.TryCreate(trimmed, out var parsed)
            && parsed.Address == trimmed;
    }
}
