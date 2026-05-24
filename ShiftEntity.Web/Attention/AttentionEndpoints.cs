using System;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Attention;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.EFCore.Attention;
using ShiftSoftware.ShiftEntity.EFCore.Entities;

namespace ShiftSoftware.ShiftEntity.Web.Attention;

public sealed class ClearAttentionRequest
{
    public required string EntityType { get; set; }
    public required long EntityId { get; set; }
}

public static class AttentionEndpoints
{
    public static IEndpointRouteBuilder MapAttentionEndpoints<TDbContext>(
        this IEndpointRouteBuilder endpoints,
        string prefix = "api/attention")
        where TDbContext : ShiftDbContext
    {
        endpoints.MapPost($"{prefix}/clear", async (
            TDbContext db,
            IdentityClaimProvider identityClaimProvider,
            [FromBody] ClearAttentionRequest request) =>
        {
            long? userId = identityClaimProvider.GetUserID();

            try
            {
                await AttentionPipeline.ClearSignals(db, request.EntityType, request.EntityId, userId);
                return Results.Ok();
            }
            catch (InvalidOperationException ex)
            {
                return Results.NotFound(ex.Message);
            }
        }).RequireAuthorization();

        endpoints.MapGet($"{prefix}/active", async (TDbContext db) =>
        {
            var signals = await db.Set<AttentionSignalEntry>()
                .Where(x => x.ClearedAt == null)
                .OrderByDescending(x => x.Severity)
                .ThenByDescending(x => x.RaisedAt)
                .Select(x => new StoredAttentionSignal
                {
                    Id = x.ID,
                    EntityType = x.EntityType,
                    EntityId = x.EntityId,
                    Source = x.Source,
                    Category = x.Category,
                    Reason = x.Reason,
                    Severity = x.Severity,
                    RaisedAt = x.RaisedAt,
                })
                .ToListAsync();

            return Results.Ok(signals);
        }).RequireAuthorization();

        return endpoints;
    }

}
