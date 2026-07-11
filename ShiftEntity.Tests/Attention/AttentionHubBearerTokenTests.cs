using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ShiftSoftware.ShiftEntity.Core.Attention;
using ShiftSoftware.ShiftEntity.Web.Attention;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Attention;

/// <summary>
/// The JwtBearer WebSocket handling that <c>AddAttentionHub</c> registers by default. Browsers
/// cannot set the <c>Authorization</c> header on WebSockets, so SignalR clients send the JWT
/// as the <c>access_token</c> query parameter, and the framework uses that value as the bearer
/// token. It does this only for hub-path requests, and only when nothing else set a token.
/// A host-assigned <c>OnMessageReceived</c> is always called first, never replaced.
/// </summary>
public class AttentionHubBearerTokenTests
{
    /// <summary>The JwtBearer options as the host would resolve them, with all post-configuration applied.</summary>
    private static JwtBearerOptions ResolveJwtOptions(IServiceCollection services)
    {
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);
    }

    /// <summary>Runs the configured <c>OnMessageReceived</c> against a request built for the test.</summary>
    private static async Task<MessageReceivedContext> RunOnMessageReceived(
        JwtBearerOptions jwtOptions,
        string path,
        string? accessTokenQueryValue)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = path;

        if (accessTokenQueryValue is not null)
            httpContext.Request.QueryString = new QueryString($"?access_token={accessTokenQueryValue}");

        var scheme = new AuthenticationScheme(
            JwtBearerDefaults.AuthenticationScheme, displayName: null, typeof(JwtBearerHandler));

        var context = new MessageReceivedContext(httpContext, scheme, jwtOptions);
        await jwtOptions.Events.OnMessageReceived(context);
        return context;
    }

    [Fact]
    public async Task PromotesAccessTokenQueryParameter_ForHubPathRequests()
    {
        var services = new ServiceCollection();
        services.AddAttentionHub();
        var jwtOptions = ResolveJwtOptions(services);

        var context = await RunOnMessageReceived(jwtOptions, AttentionRealtime.DefaultHubRoute, "the-jwt");

        Assert.Equal("the-jwt", context.Token);
    }

    [Fact]
    public async Task DoesNotPromote_OffTheHubPath()
    {
        // Ordinary API calls carry the JWT in the Authorization header. A query-string token
        // outside the hub path is never accepted.
        var services = new ServiceCollection();
        services.AddAttentionHub();
        var jwtOptions = ResolveJwtOptions(services);

        var context = await RunOnMessageReceived(jwtOptions, "/api/some-endpoint", "the-jwt");

        Assert.Null(context.Token);
    }

    [Fact]
    public async Task DoesNotPromote_WithoutAQueryToken()
    {
        var services = new ServiceCollection();
        services.AddAttentionHub();
        var jwtOptions = ResolveJwtOptions(services);

        var context = await RunOnMessageReceived(jwtOptions, AttentionRealtime.DefaultHubRoute, accessTokenQueryValue: null);

        Assert.Null(context.Token);
    }

    [Fact]
    public async Task ChainsAHostAssignedDelegate_AndItsTokenWins()
    {
        // The host's own OnMessageReceived runs first, and a token it sets is never
        // overwritten. Here it is registered AFTER AddAttentionHub, which works because
        // PostConfigureAll combines the delegates no matter which one is registered first.
        var hostDelegateRan = false;
        var services = new ServiceCollection();
        services.AddAttentionHub();
        services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
            o.Events.OnMessageReceived = context =>
            {
                hostDelegateRan = true;
                context.Token = "host-token";
                return Task.CompletedTask;
            });
        var jwtOptions = ResolveJwtOptions(services);

        var context = await RunOnMessageReceived(jwtOptions, AttentionRealtime.DefaultHubRoute, "query-token");

        Assert.True(hostDelegateRan);
        Assert.Equal("host-token", context.Token);
    }

    [Fact]
    public async Task ChainsAHostAssignedDelegate_AndStillPromotesWhenItSetsNoToken()
    {
        var hostDelegateRan = false;
        var services = new ServiceCollection();
        services.AddAttentionHub();
        services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
            o.Events.OnMessageReceived = _ =>
            {
                hostDelegateRan = true;
                return Task.CompletedTask;
            });
        var jwtOptions = ResolveJwtOptions(services);

        var context = await RunOnMessageReceived(jwtOptions, AttentionRealtime.DefaultHubRoute, "query-token");

        Assert.True(hostDelegateRan);
        Assert.Equal("query-token", context.Token);
    }

    [Fact]
    public async Task RespectsACustomHubPath()
    {
        var services = new ServiceCollection();
        services.AddAttentionHub(o => o.HubPath = "/realtime/attention");
        var jwtOptions = ResolveJwtOptions(services);

        var onCustomPath = await RunOnMessageReceived(jwtOptions, "/realtime/attention", "the-jwt");
        var onDefaultPath = await RunOnMessageReceived(jwtOptions, AttentionRealtime.DefaultHubRoute, "the-jwt");

        Assert.Equal("the-jwt", onCustomPath.Token);
        Assert.Null(onDefaultPath.Token);
    }

    [Fact]
    public async Task OptOut_LeavesJwtBearerOptionsUntouched()
    {
        var services = new ServiceCollection();
        services.AddAttentionHub(o => o.EnableWebSocketBearerToken = false);
        var jwtOptions = ResolveJwtOptions(services);

        var context = await RunOnMessageReceived(jwtOptions, AttentionRealtime.DefaultHubRoute, "the-jwt");

        Assert.Null(context.Token);
    }
}
