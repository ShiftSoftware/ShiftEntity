using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.EFCore.Attention;

namespace ShiftSoftware.ShiftEntity.Web.Attention;

public sealed class ClearAttentionRequest
{
    public required string EntityType { get; set; }
    public required long EntityId { get; set; }
}

public static class AttentionEndpoints
{
    public static IEndpointRouteBuilder MapAttentionClearEndpoint<TDbContext>(
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

        return endpoints;
    }
}
