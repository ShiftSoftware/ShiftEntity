using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ShiftSoftware.ShiftEntity.Core.Attention;
using ShiftSoftware.ShiftEntity.Web.Attention;

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
    /// if you need to configure protocols or limits.
    /// </remarks>
    public static IServiceCollection AddAttentionHub(this IServiceCollection services)
    {
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

        return services;
    }
}
