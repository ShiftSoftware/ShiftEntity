using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Attention;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.EFCore.Attention;
using ShiftSoftware.ShiftEntity.EFCore.Entities;
using System;
using System.Linq;

namespace ShiftSoftware.ShiftEntity.Web.Attention;

/// <summary>
/// Request payload for the standalone attention clear endpoint (<c>POST {prefix}/clear</c>).
/// Entity IDs are hash-encoded.
/// </summary>
public sealed class ClearAttentionRequest
{
    /// <summary>CLR type name of the entity whose signals should be cleared.</summary>
    public required string EntityType { get; set; }

    /// <summary>Hash-encoded entity ID. Decoded via <see cref="ShiftEntityDtoMap"/> to resolve the DTO type.</summary>
    public required string EntityId { get; set; }

    /// <summary>
    /// Which signals to clear. <c>null</c> clears every active signal (the default); a scoped or
    /// per-signal filter clears only the matching subset and leaves the rest active.
    /// </summary>
    public AttentionClearFilter? Filter { get; set; }
}

/// <summary>
/// Standalone (non-controller) attention endpoints for cross-entity operations.
/// Supplements the per-entity controller endpoints on <c>ShiftEntitySecureControllerAsync</c>.
/// </summary>
public static class AttentionEndpoints
{
    /// <summary>
    /// Maps standalone attention endpoints: <c>POST {prefix}/clear</c> (clears signals for a
    /// specific entity) and <c>GET {prefix}/active</c> (returns all uncleared indexed-mode
    /// signals with hash-encoded entity IDs). Both require authorization.
    /// </summary>
    public static IEndpointRouteBuilder MapAttentionEndpoints<TDbContext>(
        this IEndpointRouteBuilder endpoints,
        string prefix = "api/attention")
        where TDbContext : ShiftDbContext
    {
        var entityDtoMap = endpoints.ServiceProvider.GetRequiredService<ShiftEntityDtoMap>();

        endpoints.MapPost($"{prefix}/clear", async (
            TDbContext db,
            IHashIdService hashIdService,
            IdentityClaimProvider identityClaimProvider,
            IServiceProvider serviceProvider,
            [FromBody] ClearAttentionRequest request) =>
        {
            var dtoType = entityDtoMap.GetDtoType(request.EntityType);
            if (dtoType is null)
                return Results.NotFound($"Unknown entity type: {request.EntityType}");

            var entityId = hashIdService.Decode(request.EntityId, dtoType);
            long? userId = identityClaimProvider.GetUserID();

            try
            {
                var lastSaveDate = await AttentionPipeline.ClearSignals(db, request.EntityType, entityId, userId, request.Filter);

                // Clearing raises no AttentionRaised event — push a real-time hint so other
                // sessions drop the indicator. Best-effort + opt-in (skipped when the hub isn't
                // registered; a failed send never fails the clear).
                var broadcaster = serviceProvider.GetService<IAttentionRealtimeBroadcaster>();
                if (broadcaster is not null)
                {
                    // Exclude the window that performed the clear — it already dropped its own banner.
                    var origin = serviceProvider.GetService<IAttentionOriginProvider>()?.OriginConnectionId;
                    try { await broadcaster.BroadcastClearedAsync(request.EntityType, entityId, origin); }
                    catch { /* realtime hint is best-effort */ }
                }

                // Same contract as the per-entity controller endpoint: return the post-clear
                // audit stamp so clients can keep their loaded DTO's concurrency version current.
                return Results.Ok(new Core.Attention.ClearAttentionResponse { LastSaveDate = lastSaveDate });
            }
            catch (InvalidOperationException ex)
            {
                return Results.NotFound(ex.Message);
            }
        }).RequireAuthorization();

        endpoints.MapGet($"{prefix}/active", async (TDbContext db, IHashIdService hashIdService) =>
        {
            var entries = await db.Set<AttentionSignalEntry>()
                .Where(x => x.ClearedAt == null)
                .OrderByDescending(x => x.Severity)
                .ThenByDescending(x => x.RaisedAt)
                .ToListAsync();

            var signals = entries.Select(x =>
            {
                var signal = x.ToStoredSignal();
                var dtoType = entityDtoMap.GetDtoType(x.EntityType);
                if (dtoType is not null)
                    signal = signal with { EntityId = hashIdService.Encode(x.EntityId, dtoType) };
                return signal;
            }).ToList();

            return Results.Ok(signals);
        }).RequireAuthorization();

        return endpoints;
    }

    /// <summary>
    /// Maps the <see cref="AttentionHub"/> SignalR endpoint (default route
    /// <see cref="AttentionRealtime.DefaultHubRoute"/>). The hub itself requires authentication
    /// (<c>[Authorize]</c>). Call alongside <c>services.AddAttentionHub()</c>; apps that do
    /// neither expose no hub.
    /// </summary>
    public static HubEndpointConventionBuilder MapAttentionHub(
        this IEndpointRouteBuilder endpoints,
        string pattern = AttentionRealtime.DefaultHubRoute)
        => endpoints.MapHub<AttentionHub>(pattern);

}
