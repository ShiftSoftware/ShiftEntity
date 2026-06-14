using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Attention;
using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Web.Attention;

/// <summary>
/// The Phase 2 dispatcher consumer that turns an <see cref="AttentionRaised"/> event into a
/// real-time SignalR hint. Fans each event to the <see cref="AttentionHub"/> group for its
/// entity type (<see cref="AttentionRealtime.GroupFor"/>), where subscribed
/// <c>ShiftList</c> / <c>ShiftEntityForm</c> instances react. Registered by
/// <c>services.AddAttentionHub()</c>.
/// </summary>
/// <remarks>
/// Runs on the framework's background drain loop after the raising save has committed, like any
/// <see cref="IAttentionConsumer"/> — a slow or failing hub send never affects the save. The raw
/// <see cref="AttentionRaised.EntityId"/> is hash-encoded here, at the process boundary, via the
/// entity's DTO type from <see cref="ShiftEntityDtoMap"/>, per the HashID convention.
/// </remarks>
public sealed class AttentionRealtimeNotifier : IAttentionConsumer, IAttentionRealtimeBroadcaster
{
    private readonly IHubContext<AttentionHub> hub;
    private readonly IHashIdService hashIdService;
    private readonly ShiftEntityDtoMap dtoMap;
    private readonly ILogger<AttentionRealtimeNotifier> logger;

    public AttentionRealtimeNotifier(
        IHubContext<AttentionHub> hub,
        IHashIdService hashIdService,
        ShiftEntityDtoMap dtoMap,
        ILogger<AttentionRealtimeNotifier> logger)
    {
        this.hub = hub;
        this.hashIdService = hashIdService;
        this.dtoMap = dtoMap;
        this.logger = logger;
    }

    /// <summary>
    /// Dispatcher path: a newly-raised signal becomes a real-time <see cref="AttentionRealtimeKind.Raised"/>
    /// hint, excluding the window that raised it (<see cref="AttentionRaised.OriginConnectionId"/>).
    /// </summary>
    public Task HandleAsync(AttentionRaised attentionRaised, CancellationToken cancellationToken) =>
        SendAsync(
            attentionRaised.EntityType,
            attentionRaised.EntityId,
            AttentionRealtimeKind.Raised,
            attentionRaised.Signal.Severity,
            attentionRaised.Signal.RaisedAt,
            attentionRaised.OriginConnectionId,
            cancellationToken);

    /// <inheritdoc/>
    public Task BroadcastClearedAsync(
        string entityType,
        long entityId,
        string? originConnectionId = null,
        CancellationToken cancellationToken = default) =>
        SendAsync(
            entityType,
            entityId,
            AttentionRealtimeKind.Cleared,
            // A clear carries no meaningful severity; the client never styles a Cleared hint
            // (it drops the indicator without a toast), so Info is an inert placeholder.
            AttentionSeverity.Info,
            DateTimeOffset.UtcNow,
            originConnectionId,
            cancellationToken);

    /// <summary>
    /// Builds the hint payload and sends it to the entity-type group, excluding the originating
    /// window's connection when one was supplied so it isn't notified about its own change.
    /// </summary>
    private Task SendAsync(
        string entityType,
        long entityId,
        AttentionRealtimeKind kind,
        AttentionSeverity severity,
        DateTimeOffset raisedAt,
        string? originConnectionId,
        CancellationToken cancellationToken)
    {
        var payload = new AttentionRealtimePayload
        {
            EntityType = entityType,
            EntityId = EncodeEntityId(entityType, entityId),
            Kind = kind,
            Severity = severity,
            RaisedAt = raisedAt,
        };

        var group = AttentionRealtime.GroupFor(entityType);

        // Exclude the acting window's hub connection (it already reflects the change). With no
        // origin — a background/timer raise, or a caller not on the hub — the hint goes to the
        // whole group.
        var clients = string.IsNullOrEmpty(originConnectionId)
            ? hub.Clients.Group(group)
            : hub.Clients.GroupExcept(group, new[] { originConnectionId });

        return clients.SendAsync(AttentionRealtime.MessageName, payload, cancellationToken);
    }

    /// <summary>
    /// Hash-encodes a raw entity ID using the entity type's DTO hash configuration.
    /// </summary>
    /// <remarks>
    /// An entity with attention is normally a registered repository, so its DTO type is in
    /// <see cref="ShiftEntityDtoMap"/>. If it isn't, we can't hash-encode — rather than drop the
    /// refresh hint, we degrade to the raw ID as an invariant string (the same fallback as
    /// <c>GET api/attention/active</c>) and log a warning so the missing registration is visible.
    /// </remarks>
    private string EncodeEntityId(string entityType, long entityId)
    {
        if (dtoMap.TryGetDtoType(entityType, out var dtoType))
            return hashIdService.Encode(entityId, dtoType);

        logger.LogWarning(
            "No DTO type registered for entity type {EntityType}; the real-time attention hint " +
            "falls back to the un-encoded entity ID. Register the entity's repository so its ID " +
            "hash-encodes per the HashID convention.",
            entityType);

        return entityId.ToString(CultureInfo.InvariantCulture);
    }
}
