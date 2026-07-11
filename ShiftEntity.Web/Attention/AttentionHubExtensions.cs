using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ShiftSoftware.ShiftEntity.Core.Attention;
using ShiftSoftware.ShiftEntity.Web.Attention;
using System;

namespace Microsoft.Extensions.DependencyInjection;

public static class AttentionHubServiceCollectionExtensions
{
    /// <summary>
    /// Registers the real-time attention surface: SignalR (so <see cref="IHubContext{THub}"/>
    /// resolves) plus <c>AttentionRealtimeNotifier</c> as an attention consumer (which also
    /// brings in the emission dispatcher). After this, every committed save that raises a signal
    /// pushes an <see cref="AttentionRealtimePayload"/> to the <c>AttentionHub</c> group for the
    /// entity type. Pair with <c>endpoints.MapAttentionHub()</c> to expose the hub endpoint.
    /// </summary>
    /// <remarks>
    /// Opt-in: apps that don't call this expose no hub endpoint and get no notifier in their DI
    /// graph. Idempotent — <c>AddSignalR()</c> and the consumer registration are both safe to
    /// call alongside an app's own <c>AddSignalR()</c>; call <c>AddSignalR()</c> yourself first
    /// if you need to configure protocols or limits. This overload uses the default
    /// <see cref="AttentionHubOptions"/>. The defaults include the JwtBearer WebSocket token
    /// handling. Use the configuring overload to turn that off, or to match a custom hub route.
    /// </remarks>
    public static IServiceCollection AddAttentionHub(this IServiceCollection services)
        => services.AddAttentionHub(configure: null);

    /// <summary>
    /// <inheritdoc cref="AddAttentionHub(IServiceCollection)" path="/summary"/>
    /// <paramref name="configure"/> modifies the default <see cref="AttentionHubOptions"/>.
    /// For example, disable <see cref="AttentionHubOptions.EnableWebSocketBearerToken"/> when
    /// the host reads the query-string token itself, or set
    /// <see cref="AttentionHubOptions.HubPath"/> to a custom hub route.
    /// </summary>
    public static IServiceCollection AddAttentionHub(
        this IServiceCollection services,
        Action<AttentionHubOptions>? configure)
    {
        var options = new AttentionHubOptions();
        configure?.Invoke(options);

        services.AddSignalR();
        services.AddAttentionConsumer<AttentionRealtimeNotifier>();

        // The same notifier instance type also broadcasts clears (which raise no AttentionRaised
        // event) so other sessions drop the indicator when a signal is acknowledged. Resolved by
        // the clear endpoints; absent (and skipped) when the hub isn't registered.
        services.TryAddScoped<IAttentionRealtimeBroadcaster, AttentionRealtimeNotifier>();

        // Lets the pipeline read the originating window's hub connection id (stamped on the
        // request via AttentionRealtime.OriginHeader) so the acting window is excluded from the
        // hint it produced. Singleton — it only wraps IHttpContextAccessor (itself a singleton
        // bridging the ambient request), so ShiftRepository can resolve it from the root
        // application provider while materializing raised events.
        services.AddHttpContextAccessor();
        services.TryAddSingleton<IAttentionOriginProvider, HttpHeaderAttentionOriginProvider>();

        // Hosts that use the hub get presence with no extra setup: the hub's
        // StartViewingEntity/StopViewingEntity update this tracker, and evaluators can read it
        // to skip raising a signal the viewing user would acknowledge right away. Reading it is
        // optional; when no tracker is registered, evaluators raise signals as normal.
        // TryAdd: a host with its own real-time hubs can register its own tracker and update
        // it from those hubs.
        services.TryAddSingleton<IEntityViewerTracker, InMemoryEntityViewerTracker>();

        if (options.EnableWebSocketBearerToken)
            AddWebSocketBearerToken(services, options.HubPath);

        return services;
    }

    /// <summary>
    /// For requests under the hub path, uses the <c>access_token</c> query parameter as the
    /// JwtBearer token. Browsers cannot set the <c>Authorization</c> header on WebSockets,
    /// so SignalR clients send the JWT in the query string instead.
    /// </summary>
    /// <remarks>
    /// This uses <c>PostConfigureAll</c>. Because of that, it works together with
    /// <c>AddJwtBearer</c>/<c>Configure</c> calls, whether they run before or after
    /// <c>AddAttentionHub</c>. When JwtBearer is not configured at all, it does nothing: the
    /// post-configure simply never runs against a resolved scheme. Any previously assigned
    /// <c>OnMessageReceived</c> is chained, not replaced. The previous delegate runs first,
    /// and if it sets a token, that token is kept. Two limits to know about. First, this
    /// query-token handling applies to <em>every</em> registered JwtBearer scheme. It only
    /// acts on requests under the hub path, and only when no token was set yet, so other
    /// schemes are not affected outside those two conditions. Second, a host that assigns
    /// <c>OnMessageReceived</c> inside its own <c>PostConfigure</c> registered <em>after</em>
    /// <c>AddAttentionHub</c> replaces this handling, because post-configures run in
    /// registration order. Such a host should call the previous delegate from its own, or
    /// disable
    /// <see cref="ShiftSoftware.ShiftEntity.Web.Attention.AttentionHubOptions.EnableWebSocketBearerToken"/>
    /// and add the query-token handling itself.
    /// </remarks>
    private static void AddWebSocketBearerToken(IServiceCollection services, string hubPath)
    {
        var path = new PathString(hubPath);

        services.PostConfigureAll<JwtBearerOptions>(jwtOptions =>
        {
            jwtOptions.Events ??= new JwtBearerEvents();
            var previous = jwtOptions.Events.OnMessageReceived;

            jwtOptions.Events.OnMessageReceived = async context =>
            {
                if (previous is not null)
                    await previous(context);

                if (!string.IsNullOrEmpty(context.Token))
                    return;

                string? accessToken = context.Request.Query["access_token"];

                if (!string.IsNullOrEmpty(accessToken) && context.Request.Path.StartsWithSegments(path))
                    context.Token = accessToken;
            };
        });
    }
}
