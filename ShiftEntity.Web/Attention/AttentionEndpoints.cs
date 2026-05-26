using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.Core;
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
            [FromBody] ClearAttentionRequest request) =>
        {
            var dtoType = entityDtoMap.GetDtoType(request.EntityType);
            if (dtoType is null)
                return Results.NotFound($"Unknown entity type: {request.EntityType}");

            var entityId = hashIdService.Decode(request.EntityId, dtoType);
            long? userId = identityClaimProvider.GetUserID();

            try
            {
                await AttentionPipeline.ClearSignals(db, request.EntityType, entityId, userId);
                return Results.Ok();
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

}
