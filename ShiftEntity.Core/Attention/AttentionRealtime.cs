namespace ShiftSoftware.ShiftEntity.Core.Attention;

/// <summary>
/// Shared constants for the attention real-time channel — the SignalR message name, the hub
/// group-naming scheme, and the default hub route. Centralised here (in <c>ShiftEntity.Core</c>,
/// referenced by both the server <c>ShiftEntity.Web</c> and the client <c>ShiftBlazor</c>) so the
/// hub, the notifier, and the client switches can never drift on a magic string.
/// </summary>
public static class AttentionRealtime
{
    /// <summary>
    /// SignalR method name the server invokes on clients and the client handles. Carries one
    /// <see cref="AttentionRealtimePayload"/> argument.
    /// </summary>
    public const string MessageName = "AttentionRaised";

    /// <summary>
    /// Prefix for the per-entity-type SignalR group. Combined with the entity type name via
    /// <see cref="GroupFor"/>; groups are entity-type only (never per-row), so a hint can be
    /// addressed without exposing row identity in the group name.
    /// </summary>
    public const string GroupPrefix = "attention:";

    /// <summary>
    /// Default route the hub is mapped at by <c>MapAttentionHub</c> and that clients connect to.
    /// </summary>
    public const string DefaultHubRoute = "/hubs/attention";

    /// <summary>
    /// Request header a client stamps on a mutating call (save / delete / clear) with its own
    /// <c>AttentionHub</c> connection id, so the server can exclude that connection from the
    /// resulting real-time hint (<c>Clients.GroupExcept</c>). This is what stops the window that
    /// performed the action from being notified about its own change — it already reflects it.
    /// Absent when the caller holds no hub connection (nothing to exclude, and that window isn't
    /// subscribed either, so no echo can reach it).
    /// </summary>
    public const string OriginHeader = "X-Attention-Origin";

    /// <summary>
    /// The SignalR group name for an entity type (e.g. <c>"attention:Invoice"</c>). Used by both
    /// the hub's subscribe/unsubscribe methods and the notifier's send target.
    /// </summary>
    public static string GroupFor(string entityType) => GroupPrefix + entityType;
}
