namespace ShiftSoftware.ShiftEntity.Core.Attention;

/// <summary>
/// The event published for each newly-raised (post-dedup) attention signal, after the save
/// that raised it has committed. One save can publish several of these — one per signal —
/// and a signal suppressed by dedup (same <c>(Source, Category)</c> still active within the
/// re-raise window) publishes nothing. Consumers subscribe by implementing
/// <see cref="IAttentionConsumer"/>.
/// </summary>
/// <remarks>
/// Published <em>after</em> the database transaction commits, so consumers never observe a
/// signal that subsequently rolled back. Clearing a signal does not publish an event.
/// </remarks>
public sealed record AttentionRaised
{
    /// <summary>
    /// CLR type name of the entity that raised the signal (e.g. <c>"Invoice"</c>). Matches
    /// the <c>EntityType</c> discriminator used by the indexed <c>AttentionSignals</c> table.
    /// </summary>
    public required string EntityType { get; init; }

    /// <summary>
    /// Raw database ID of the entity that raised the signal.
    /// </summary>
    /// <remarks>
    /// This is the internal <c>long</c> ID — safe for in-process use (queries, joins,
    /// logging). Any consumer that serializes it to a client (SignalR payload, email deep
    /// link, webhook body) must hash-encode it first via <c>IHashIdService</c> and the
    /// entity's DTO type (resolvable through <c>ShiftEntityDtoMap</c>), per the framework's
    /// HashID convention — raw IDs must never leave the process.
    /// </remarks>
    public required long EntityId { get; init; }

    /// <summary>
    /// The signal as persisted: <c>Source</c>, <c>Category</c>, <c>Reason</c>,
    /// <c>Severity</c>, <c>PayloadJson</c>, and <c>RaisedAt</c>.
    /// </summary>
    /// <remarks>
    /// The signal's own <c>EntityType</c> / <c>EntityId</c> properties are <c>null</c> here —
    /// those are populated only when reading rows back from the indexed table. The event's
    /// <see cref="EntityType"/> and <see cref="EntityId"/> are the authoritative identity.
    /// </remarks>
    public required StoredAttentionSignal Signal { get; init; }

    /// <summary>
    /// The <c>AttentionHub</c> connection id of the window that performed the save, when it was
    /// supplied (the client stamps it via <see cref="AttentionRealtime.OriginHeader"/>). A
    /// real-time consumer that fans the event out over SignalR excludes this connection so the
    /// originating window isn't notified about its own change. <c>null</c> for saves with no
    /// originating hub connection — a background job, a timer, or a client not on the hub — in
    /// which case the hint goes to the whole group.
    /// </summary>
    /// <remarks>
    /// Captured during the save (while the request context is still current) and carried on the
    /// event because the real-time fan-out runs later on a background drain loop, detached from
    /// the originating request — the connection id can't be recovered at broadcast time.
    /// </remarks>
    public string? OriginConnectionId { get; init; }
}
