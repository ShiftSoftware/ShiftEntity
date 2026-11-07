using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Core.Notifications;

/// <summary>One label and value in an email's details. A row with an empty value is left out.</summary>
public sealed record NotificationEmailDetail(string Label, string? Value);

/// <summary>What an email says. The shared layout turns these fields into HTML and plain text.</summary>
public sealed class NotificationEmailContent
{
    public required string Subject { get; init; }

    public required string Headline { get; init; }

    /// <summary>The label and value rows, in order.</summary>
    public IReadOnlyList<NotificationEmailDetail> Details { get; init; } = [];

    /// <summary>The text of the button. The button appears when this and <see cref="ButtonUrl"/> are both set.</summary>
    public string? ButtonText { get; init; }

    /// <summary>The link of the button, as an absolute http or https URL. Any other link leaves the button out.</summary>
    public string? ButtonUrl { get; init; }

    /// <summary>
    /// The sentence above the fallback link under the button, in the email's language. English when left out.
    /// </summary>
    public string? ButtonFallbackText { get; init; }
}

/// <summary>
/// Fills in an email's fields for the topics of one entity type. The host implements this and registers it as a scoped
/// service. It runs in the event's own scope, in the culture of the save that raised the event, and with no signed-in
/// user.
/// <para>
/// <b>Protected fields stay masked.</b> An email can be forwarded, and it has no reveal step. When this maps the record
/// to a DTO with protected fields, call <c>PiiDtoProtector.Protect(dto)</c> and use each field's <c>Display</c>. That
/// needs no signed-in user. A map alone leaves the raw value in <c>Value</c>, so never use <c>Value</c> here. The
/// record's own properties are raw, so writing one of them into an email is a choice to send it unmasked.
/// </para>
/// </summary>
public interface INotificationEmailContent<in TEntity>
{
    /// <summary>The email's fields, or null to send nothing for this event. That is logged.</summary>
    ValueTask<NotificationEmailContent?> BuildAsync(NotificationTopic topic, TEntity record, CancellationToken cancellationToken = default);
}
