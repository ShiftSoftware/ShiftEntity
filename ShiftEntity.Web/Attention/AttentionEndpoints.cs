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
using ShiftSoftware.TypeAuth.Core;
using ShiftSoftware.TypeAuth.Core.Actions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

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
/// The kind of access an <see cref="AttentionEndpointsOptions.AuthorizeEntityType"/> hook is
/// asked to allow or deny for one entity type.
/// </summary>
public enum AttentionEndpointAccess
{
    /// <summary>Reading the entity type's active signals (<c>GET {prefix}/active</c>).</summary>
    Read,

    /// <summary>Clearing the entity type's signals (<c>POST {prefix}/clear</c>).</summary>
    Clear,
}

/// <summary>
/// What the standalone attention endpoints do with signals of an entity type that has no entry
/// in the <see cref="ShiftEntityActionMap"/> (and no
/// <see cref="AttentionEndpointsOptions.AuthorizeEntityType"/> hook is set).
/// </summary>
public enum AttentionUnmappedEntityTypeAccess
{
    /// <summary>
    /// The default. Signals of an unmapped type are left out of <c>GET {prefix}/active</c>, and
    /// <c>POST {prefix}/clear</c> for an unmapped type returns 403.
    /// </summary>
    Deny,

    /// <summary>
    /// Any authenticated user can read and clear signals of an unmapped type. Only use this when
    /// every authenticated user is trusted to see and clear those signals.
    /// </summary>
    AllowAuthenticated,
}

/// <summary>Options for <see cref="AttentionEndpoints.MapAttentionEndpoints"/>.</summary>
/// <remarks>
/// Per-entity-type authorization runs in this order:
/// <list type="number">
///   <item>When <see cref="AuthorizeEntityType"/> is set, it alone decides. The registry and
///   <see cref="UnmappedEntityTypeAccess"/> are not consulted at all.</item>
///   <item>Otherwise, when the <see cref="ShiftEntityActionMap"/> registry has an action for the
///   signal's entity type, that action is checked with TypeAuth: reading the type's signals
///   requires <c>CanRead</c>, clearing them requires <c>CanWrite</c> — the same checks the
///   entity's own secure endpoints apply.</item>
///   <item>Otherwise (no hook, no registry entry) <see cref="UnmappedEntityTypeAccess"/>
///   decides. The default is <see cref="AttentionUnmappedEntityTypeAccess.Deny"/>.</item>
/// </list>
/// The registry is fed automatically by secure attribute endpoints
/// (<c>[ShiftEntitySecureEndpoint&lt;…&gt;]</c>, via <c>RegisterShiftRepositories</c>) and by
/// <c>MapShiftEntitySecureCrud</c> when it is called with a non-null action. Entities served by
/// a classic <c>ShiftEntitySecureControllerAsync</c> must be registered explicitly with
/// <c>services.AddShiftEntityAction&lt;TEntity&gt;(action)</c>, because the controller receives
/// its action through its constructor and the framework cannot see it at startup.
/// </remarks>
public sealed class AttentionEndpointsOptions
{
    /// <summary>
    /// An authorization hook that decides access per entity type — the full override for special
    /// cases the registry cannot express. It receives the request's <see cref="HttpContext"/>
    /// (resolve your authorization service from it), the signal's entity CLR type name, and the
    /// kind of access being requested. When the hook is set, it alone decides: the
    /// <see cref="ShiftEntityActionMap"/> registry and <see cref="UnmappedEntityTypeAccess"/> are
    /// not consulted. <c>GET {prefix}/active</c> leaves out signals of every type for which the
    /// hook denies <see cref="AttentionEndpointAccess.Read"/> (the hook runs once per distinct
    /// type in a request, not once per signal). <c>POST {prefix}/clear</c> returns 403 when the
    /// hook denies <see cref="AttentionEndpointAccess.Clear"/> for the requested type.
    /// </summary>
    /// <remarks>
    /// When <c>null</c> (the default), access is decided by the <see cref="ShiftEntityActionMap"/>
    /// registry, and for entity types without a registry entry by
    /// <see cref="UnmappedEntityTypeAccess"/>. See the class remarks for the full order.
    /// </remarks>
    public Func<HttpContext, string, AttentionEndpointAccess, ValueTask<bool>>? AuthorizeEntityType { get; set; }

    /// <summary>
    /// What happens for signals of an entity type that has no entry in the
    /// <see cref="ShiftEntityActionMap"/> registry, when no <see cref="AuthorizeEntityType"/>
    /// hook is set. The default is <see cref="AttentionUnmappedEntityTypeAccess.Deny"/>: such
    /// signals are left out of <c>GET {prefix}/active</c>, and <c>POST {prefix}/clear</c>
    /// returns 403 for them.
    /// </summary>
    public AttentionUnmappedEntityTypeAccess UnmappedEntityTypeAccess { get; set; } = AttentionUnmappedEntityTypeAccess.Deny;
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
    /// signals with hash-encoded entity IDs). Both require an authenticated user, and access is
    /// then decided per entity type: by the <see cref="AttentionEndpointsOptions.AuthorizeEntityType"/>
    /// hook when one is supplied through the configuring overload; otherwise by the TypeAuth
    /// action registered for the type in the <see cref="ShiftEntityActionMap"/> (read requires
    /// <c>CanRead</c>, clear requires <c>CanWrite</c>); otherwise by
    /// <see cref="AttentionEndpointsOptions.UnmappedEntityTypeAccess"/>, which denies by default.
    /// See the <see cref="AttentionEndpointsOptions"/> remarks for the full order and for how
    /// the registry is fed.
    /// </summary>
    // This stays a separate overload on purpose. We do not merge `configure` into it as an
    // optional parameter. This exact signature is already shipped in the compiled package,
    // and consumer assemblies built against it call this exact method.
    public static IEndpointRouteBuilder MapAttentionEndpoints<TDbContext>(
        this IEndpointRouteBuilder endpoints,
        string prefix = "api/attention")
        where TDbContext : ShiftDbContext
        => MapAttentionEndpoints<TDbContext>(endpoints, prefix, configure: null);

    /// <summary>
    /// <inheritdoc cref="MapAttentionEndpoints{TDbContext}(IEndpointRouteBuilder, string)" path="/summary"/>
    /// <paramref name="configure"/> modifies the default <see cref="AttentionEndpointsOptions"/>.
    /// Its main purpose is to supply <see cref="AttentionEndpointsOptions.AuthorizeEntityType"/>.
    /// </summary>
    public static IEndpointRouteBuilder MapAttentionEndpoints<TDbContext>(
        this IEndpointRouteBuilder endpoints,
        string prefix,
        Action<AttentionEndpointsOptions>? configure)
        where TDbContext : ShiftDbContext
    {
        var entityDtoMap = endpoints.ServiceProvider.GetRequiredService<ShiftEntityDtoMap>();

        var options = new AttentionEndpointsOptions();
        configure?.Invoke(options);

        endpoints.MapPost($"{prefix}/clear", async (
            HttpContext httpContext,
            TDbContext db,
            IHashIdService hashIdService,
            IdentityClaimProvider identityClaimProvider,
            IServiceProvider serviceProvider,
            [FromBody] ClearAttentionRequest request) =>
        {
            // The per-entity-type authorization check (hook → action registry → unmapped
            // default; see AttentionEndpointsOptions). It runs before the request is
            // processed in any way, and before any lookup result is returned. A denied
            // caller therefore learns nothing about the entity or its signals.
            if (!await IsEntityTypeAllowed(httpContext, options, request.EntityType, AttentionEndpointAccess.Clear))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

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

        endpoints.MapGet($"{prefix}/active", async (HttpContext httpContext, TDbContext db, IHashIdService hashIdService) =>
        {
            var entries = await db.Set<AttentionSignalEntry>()
                .Where(x => x.ClearedAt == null)
                .OrderByDescending(x => x.Severity)
                .ThenByDescending(x => x.RaisedAt)
                .ToListAsync();

            // The per-entity-type authorization check (hook → action registry → unmapped
            // default; see AttentionEndpointsOptions): remove the types this caller may not
            // read. The check runs once per distinct type, not once per signal.
            var allowedTypes = new Dictionary<string, bool>(StringComparer.Ordinal);

            foreach (var entityType in entries.Select(x => x.EntityType).Distinct(StringComparer.Ordinal))
                allowedTypes[entityType] = await IsEntityTypeAllowed(httpContext, options, entityType, AttentionEndpointAccess.Read);

            entries = entries.Where(x => allowedTypes[x.EntityType]).ToList();

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
    /// The per-entity-type access decision, in the order documented on
    /// <see cref="AttentionEndpointsOptions"/>: the <see cref="AttentionEndpointsOptions.AuthorizeEntityType"/>
    /// hook alone when set; otherwise the TypeAuth action registered in the
    /// <see cref="ShiftEntityActionMap"/> (<see cref="ITypeAuthService.CanRead(ReadWriteDeleteAction)"/>
    /// for read, <see cref="ITypeAuthService.CanWrite(ReadWriteDeleteAction)"/> for clear — the
    /// same calls the secure controller makes); otherwise
    /// <see cref="AttentionEndpointsOptions.UnmappedEntityTypeAccess"/>.
    /// </summary>
    /// <remarks>
    /// The registry is resolved with <c>GetService</c>: when no <see cref="ShiftEntityActionMap"/>
    /// is registered at all, the behavior is the same as an empty map — every type is unmapped.
    /// </remarks>
    internal static async ValueTask<bool> IsEntityTypeAllowed(
        HttpContext httpContext,
        AttentionEndpointsOptions options,
        string entityType,
        AttentionEndpointAccess access)
    {
        if (options.AuthorizeEntityType is { } authorize)
            return await authorize(httpContext, entityType, access);

        var actionMap = httpContext.RequestServices.GetService<ShiftEntityActionMap>();

        if (actionMap is not null && actionMap.TryGetAction(entityType, out var action))
        {
            var typeAuthService = httpContext.RequestServices.GetRequiredService<ITypeAuthService>();

            return access == AttentionEndpointAccess.Read
                ? typeAuthService.CanRead(action)
                : typeAuthService.CanWrite(action);
        }

        return options.UnmappedEntityTypeAccess == AttentionUnmappedEntityTypeAccess.AllowAuthenticated;
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
