using System;
using System.Collections.Concurrent;
using System.Linq;

namespace ShiftSoftware.ShiftEntity.Core.Attention;

/// <summary>
/// Default <see cref="IEntityViewerTracker"/>: an in-process map from connection id to the set
/// of entries that connection is viewing. Registered (<c>TryAdd</c>) by <c>AddAttentionHub</c>
/// and updated by the <c>AttentionHub</c> viewing methods. It covers a single process on
/// purpose. Behind a scaled-out backplane the skip check is best-effort: it can miss viewers on
/// other nodes, and signals are then raised as normal (see the interface remarks).
/// A connection can hold at most 128 entries; adds beyond that are dropped. This stops one
/// client from growing the map without limit (scopes are free-form client strings), and a
/// dropped entry is safe: the record then counts as not viewed, and signals are raised as
/// normal.
/// </summary>
public sealed class InMemoryEntityViewerTracker : IEntityViewerTracker
{
    private const int MaxEntriesPerConnection = 128;

    // One entry per (entity type, entity id, scope) a connection is viewing. The record struct
    // is the dictionary key, so adding the same entry twice keeps a single entry, and removing
    // one entry is a single lookup. String members compare with exact, case-sensitive equality.
    private readonly record struct ViewerEntry(string EntityType, long EntityId, string? Scope);

    // Keyed by connection id, so RemoveConnection (the disconnect cleanup) is a single lookup.
    // The inner dictionary is used as a concurrent set; the values carry no meaning.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<ViewerEntry, byte>> viewers =
        new(StringComparer.Ordinal);

    public void AddViewer(string connectionId, string entityType, long entityId, string? scope = null)
    {
        var entries = viewers.GetOrAdd(connectionId, _ => new());
        var entry = new ViewerEntry(entityType, entityId, scope);

        // The cap check and the add are two steps, so parallel adds can go slightly over the
        // cap. That is fine — the cap is a safety limit, not an exact quota. Calls from one
        // SignalR connection run one at a time anyway. Re-adding an entry that already exists
        // stays a no-op even when the connection is at the cap.
        if (entries.Count >= MaxEntriesPerConnection && !entries.ContainsKey(entry))
            return;

        entries.TryAdd(entry, 0);
    }

    public void RemoveViewer(string connectionId, string entityType, long entityId, string? scope = null)
    {
        if (viewers.TryGetValue(connectionId, out var entries))
            entries.TryRemove(new ViewerEntry(entityType, entityId, scope), out _);
    }

    public void RemoveConnection(string connectionId)
        => viewers.TryRemove(connectionId, out _);

    // This scans all entries. There are only a few entries per connected viewer, so the set is
    // small. A reverse index would add upkeep cost without a real gain.
    public bool HasViewers(string entityType, long entityId, string? scope = null)
        => viewers.Values.Any(entries => entries.Keys.Any(entry =>
            entry.EntityId == entityId
            && string.Equals(entry.EntityType, entityType, StringComparison.Ordinal)
            && (scope is null || string.Equals(entry.Scope, scope, StringComparison.Ordinal))));
}
