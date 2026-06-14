using System;

namespace ShiftSoftware.ShiftEntity.Core.Attention;

/// <summary>
/// The minimal message pushed to connected clients over <c>AttentionHub</c> when an attention
/// signal is raised. It is a <em>refresh hint</em>, not data: it tells a subscribed list or
/// form "something on this entity type changed — re-read if you care," and the client re-reads
/// through its normal data-level access (OData query / single fetch) on reload. Because the
/// payload carries no row content, it cannot leak a row the receiving user isn't allowed to see.
/// </summary>
/// <remarks>
/// Shared by the server-side notifier (<c>AttentionRealtimeNotifier</c> in
/// <c>ShiftEntity.Web</c>) and the client-side switches (<c>ShiftList</c> /
/// <c>ShiftEntityForm</c> in <c>ShiftBlazor</c>), so both sides bind the identical SignalR
/// message shape. <see cref="EntityId"/> is hash-encoded before the payload leaves the
/// process — see the property remarks.
/// </remarks>
public sealed record AttentionRealtimePayload
{
    /// <summary>
    /// CLR type name of the entity that raised the signal (e.g. <c>"Invoice"</c>). Matches the
    /// group a client subscribes to via <c>AttentionHub.SubscribeToEntityType</c> and the
    /// <c>EntityType</c> discriminator on the indexed <c>AttentionSignals</c> table.
    /// </summary>
    public required string EntityType { get; init; }

    /// <summary>
    /// Hash-encoded entity ID. Already encoded per the framework's HashID convention (via the
    /// entity's DTO type, resolved through <c>ShiftEntityDtoMap</c>), so it matches the IDs the
    /// client already holds for its loaded rows and can be compared directly — a form open on a
    /// record compares this against its own hashed key to decide whether the hint applies to it.
    /// The raw internal <c>long</c> ID never appears here.
    /// </summary>
    public required string EntityId { get; init; }

    /// <summary>
    /// Whether this hint is a raise or a clear (acknowledgement). Both are refresh hints — the
    /// client re-reads on either — but only <see cref="AttentionRealtimeKind.Raised"/> shows a
    /// toast; a <see cref="AttentionRealtimeKind.Cleared"/> hint drops the indicator silently.
    /// </summary>
    public required AttentionRealtimeKind Kind { get; init; }

    /// <summary>Severity of the raised signal — lets a client style a toast without a re-fetch.</summary>
    public required AttentionSeverity Severity { get; init; }

    /// <summary>When the signal was raised.</summary>
    public required DateTimeOffset RaisedAt { get; init; }
}
