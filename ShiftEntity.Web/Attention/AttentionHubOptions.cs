using ShiftSoftware.ShiftEntity.Core.Attention;

namespace ShiftSoftware.ShiftEntity.Web.Attention;

/// <summary>
/// Options for <c>services.AddAttentionHub(...)</c>. The parameterless overload uses the
/// defaults below.
/// </summary>
public sealed class AttentionHubOptions
{
    /// <summary>
    /// Whether to register the standard JwtBearer handling for WebSockets. Browsers cannot
    /// set the <c>Authorization</c> header on a WebSocket request, so SignalR clients send the
    /// JWT as the <c>access_token</c> query parameter. For requests under <see cref="HubPath"/>,
    /// this setting makes the framework use that query value as the bearer token. Default
    /// <c>true</c>. Without it, every JwtBearer host would have to write the same
    /// <c>OnMessageReceived</c> setup code by hand.
    /// </summary>
    /// <remarks>
    /// Works together with a host's own <c>OnMessageReceived</c>. It is applied via
    /// <c>PostConfigureAll</c> and calls any previously assigned delegate instead of replacing
    /// it. The host's delegate runs first, and if it sets a token, that token is kept. When
    /// JwtBearer authentication is not configured at all, this setting does nothing. Disable
    /// it only when the host reads the query-string token itself.
    /// </remarks>
    public bool EnableWebSocketBearerToken { get; set; } = true;

    /// <summary>
    /// The request path that the token handling matches on. The query-string token is only
    /// accepted for hub requests, never for ordinary API calls. Defaults to
    /// <see cref="AttentionRealtime.DefaultHubRoute"/>. If the hub is mapped at a custom
    /// route, keep this value equal to the pattern passed to <c>MapAttentionHub</c>.
    /// </summary>
    public string HubPath { get; set; } = AttentionRealtime.DefaultHubRoute;
}
