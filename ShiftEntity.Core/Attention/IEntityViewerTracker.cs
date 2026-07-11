namespace ShiftSoftware.ShiftEntity.Core.Attention;

/// <summary>
/// Tracks which real-time connections are viewing which entity right now. An evaluator can use
/// this to skip raising a signal that an active viewer would acknowledge right away. The viewer
/// already sees the change in real time. Raising a signal and clearing it a moment later would
/// refresh every subscribed list twice for no benefit. Entity types use the CLR short name —
/// the same names as <see cref="AttentionRaised.EntityType"/> and
/// <see cref="AttentionRealtime.GroupFor"/>.
/// </summary>
/// <remarks>
/// <para>
/// Presence is best-effort. Whenever presence cannot be checked, signals are raised as normal.
/// Evaluators resolve the tracker <em>optionally</em> from
/// <see cref="AttentionContext{TEntity}.Services"/> (<c>GetService</c>, never
/// <c>GetRequiredService</c>). When no tracker is registered, they raise as normal. The
/// in-memory implementation (<see cref="InMemoryEntityViewerTracker"/>) only sees one process.
/// Behind a scaled-out SignalR backplane, a viewer connected to another node is not visible,
/// so the skip check can miss that viewer — the signal is then raised as normal. See also
/// <see cref="AttentionViewerExtensions.HasActiveViewers{TEntity}"/>, which wraps this pattern
/// in a single call.
/// </para>
/// <para>
/// A connection may hold many viewer entries at once: different records, or the same record
/// with different scopes. An entry is identified by the full
/// (connection, entity type, entity id, scope) combination. A scope names WHICH PART of the
/// record is being viewed — for example a specific tab of a form. It uses the same free-form
/// string style as the attention clear scope. A <c>null</c> scope means the record as a whole,
/// with no named part.
/// </para>
/// <para>
/// <c>AddAttentionHub</c> registers the in-memory implementation (<c>TryAdd</c>) and the
/// <c>AttentionHub</c> updates it. A host with its own real-time hubs can register its own
/// tracker and update it from those hubs.
/// </para>
/// <para>
/// Ordering rule for hosts that update the tracker themselves: do not call
/// <see cref="AddViewer"/> for a connection at the same time as, or after, that connection's
/// <see cref="RemoveConnection"/>. A late add can re-create the connection's entry set after
/// the cleanup ran, and because the connection is already gone, no later disconnect will ever
/// remove it — the record then looks viewed forever. SignalR hubs are safe by default: a
/// connection's calls run one at a time, and the disconnect handler runs after them.
/// </para>
/// </remarks>
public interface IEntityViewerTracker
{
    /// <summary>
    /// Records that the connection is viewing the given entity (optionally a named
    /// <paramref name="scope"/> of it). The entry is added next to the connection's other
    /// entries — it does not replace them. Adding an entry that already exists does nothing.
    /// </summary>
    void AddViewer(string connectionId, string entityType, long entityId, string? scope = null);

    /// <summary>
    /// Forgets exactly one entry: the given connection's entry for the given entity and
    /// <paramref name="scope"/>. The connection's other entries are kept. The scope must match
    /// the one the entry was added with — a <c>null</c> scope only removes the <c>null</c>-scope
    /// entry. Does nothing when no such entry exists.
    /// </summary>
    void RemoveViewer(string connectionId, string entityType, long entityId, string? scope = null);

    /// <summary>
    /// Forgets every entry of the given connection. Used when the connection disconnects.
    /// Does nothing for unknown connection ids.
    /// </summary>
    void RemoveConnection(string connectionId);

    /// <summary>
    /// Whether at least one connection is currently viewing the given entity. With a
    /// <c>null</c> <paramref name="scope"/> (the default), any entry for the record counts,
    /// whatever its scope. With a non-null <paramref name="scope"/>, only entries that were
    /// added with that exact scope count (exact, case-sensitive matching). An entry that was
    /// added with a <c>null</c> scope is therefore only found by a <c>null</c>-scope query.
    /// </summary>
    bool HasViewers(string entityType, long entityId, string? scope = null);
}
