using System;
using System.Threading;
using System.Threading.Tasks;
using ShiftSoftware.ShiftEntity.Core.Attention;

namespace ShiftSoftware.ShiftEntity.Web.Attention;

/// <summary>
/// Pushes a real-time <see cref="AttentionRealtimeKind.Cleared"/> hint for one entity to the
/// <see cref="AttentionHub"/> group, hash-encoding the entity ID at the boundary. Clearing raises
/// no <see cref="AttentionRaised"/> event, so the clear endpoints invoke this directly — a
/// subscribed list/form re-reads on the hint and drops its indicator (without a toast, since the
/// hint is <see cref="AttentionRealtimeKind.Cleared"/>). The raise path goes through the
/// dispatcher consumer instead. Registered by <c>AddAttentionHub()</c>; resolve it optionally (it
/// is absent when the hub isn't registered).
/// </summary>
public interface IAttentionRealtimeBroadcaster
{
    /// <summary>
    /// Sends a <see cref="AttentionRealtimeKind.Cleared"/> hint to the entity-type group.
    /// <paramref name="entityId"/> is the raw internal ID; it is hash-encoded before the payload
    /// leaves the process. <paramref name="originConnectionId"/> — the acting window's hub
    /// connection id from <see cref="AttentionRealtime.OriginHeader"/> — is excluded from the
    /// send so that window isn't notified about the clear it performed; pass <c>null</c> to fan
    /// out to the whole group.
    /// </summary>
    Task BroadcastClearedAsync(string entityType, long entityId, string? originConnectionId = null, CancellationToken cancellationToken = default);
}
