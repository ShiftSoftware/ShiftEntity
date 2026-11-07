using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Core.Notifications;

/// <summary>One email, ready to send.</summary>
public sealed record NotificationEmailMessage(
    IReadOnlyList<string> To,
    IReadOnlyList<string> Cc,
    IReadOnlyList<string> Bcc,
    string Subject,
    string HtmlBody,
    string TextBody);

/// <summary>
/// Sends an email through whatever the host uses. The host implements this, and the framework depends on no sending
/// service. It is resolved in the event's own scope. Throwing is fine: the failure is logged and there is no retry.
/// </summary>
public interface INotificationEmailSender
{
    Task SendAsync(NotificationEmailMessage message, CancellationToken cancellationToken = default);
}
