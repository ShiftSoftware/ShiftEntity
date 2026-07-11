using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Attention;
using ShiftSoftware.ShiftEntity.EFCore.Entities;
using ShiftSoftware.ShiftEntity.Tests.Attention.Scenario;
using ShiftSoftware.ShiftEntity.Tests.DataLevelAccess.Scenario;
using ShiftSoftware.ShiftEntity.Web.Attention;
using ShiftSoftware.TypeAuth.Core;
using System.Text;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Attention;

/// <summary>
/// Per-entity-type authorization of the standalone attention endpoints
/// (<c>GET {prefix}/active</c> and <c>POST {prefix}/clear</c>), exercised through the real
/// mapped endpoints (via <see cref="TestEndpointRouteBuilder"/>) so the whole decision path
/// runs. The decision order under test — see <see cref="AttentionEndpointsOptions"/>:
/// <list type="number">
///   <item>The <see cref="AttentionEndpointsOptions.AuthorizeEntityType"/> hook, when set,
///   alone decides.</item>
///   <item>Otherwise a <see cref="ShiftEntityActionMap"/> entry: TypeAuth <c>CanRead</c> gates
///   reading, <c>CanWrite</c> gates clearing.</item>
///   <item>Otherwise <see cref="AttentionEndpointsOptions.UnmappedEntityTypeAccess"/>
///   (default <see cref="AttentionUnmappedEntityTypeAccess.Deny"/>).</item>
/// </list>
/// The TypeAuth service is faked the same way as in the data-level tests: a real
/// <see cref="TypeAuthContext"/> built from a hand-written access tree.
/// </summary>
public class AttentionEndpointAuthorizationTests
{
    // ── Harness ─────────────────────────────────────────────────────────────────

    /// <summary>A TypeAuth context granting the given access levels on the Widgets action.</summary>
    private static TypeAuthContext WidgetsGrant(params Access[] accesses)
    {
        var tree = new Dictionary<string, object>
        {
            [nameof(AttentionTestActions)] = new Dictionary<string, object>
            {
                [nameof(AttentionTestActions.Widgets)] = accesses,
            },
        };

        return new TypeAuthContextBuilder()
            .AddAccessTree(JsonConvert.SerializeObject(tree))
            .AddActionTree<AttentionTestActions>()
            .Build();
    }

    /// <summary>An action map with the Widgets action registered for <see cref="WidgetEntity"/>.</summary>
    private static ShiftEntityActionMap WidgetActionMap()
    {
        var actionMap = new ShiftEntityActionMap();
        actionMap.Register(nameof(WidgetEntity), AttentionTestActions.Widgets);
        return actionMap;
    }

    /// <summary>
    /// Builds a provider shaped like a host that mapped the attention endpoints: EF InMemory
    /// database, identity + hash-id fakes, the DTO map the endpoints require, and — depending
    /// on the test — an action map and a TypeAuth service.
    /// </summary>
    private static ServiceProvider BuildProvider(
        ShiftEntityActionMap? actionMap = null,
        ITypeAuthService? typeAuth = null)
    {
        var services = new ServiceCollection();

        services.AddLogging();

        services.AddDbContext<AttentionTestDbContext>(options => options
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));

        services.AddScoped<ICurrentUserProvider>(_ => FakeCurrentUserProvider.Anonymous());
        services.AddScoped<IdentityClaimProvider>();
        services.AddSingleton<IHashIdService>(new RecordingHashIdService());

        var dtoMap = new ShiftEntityDtoMap();
        dtoMap.Register(nameof(WidgetEntity), typeof(GadgetDTO));
        dtoMap.Register(nameof(GadgetEntity), typeof(GadgetDTO));
        services.AddSingleton(dtoMap);

        if (actionMap is not null)
            services.AddSingleton(actionMap);

        if (typeAuth is not null)
            services.AddScoped<ITypeAuthService>(_ => typeAuth);

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    /// <summary>Maps the endpoints and returns the builder to read them back from.</summary>
    private static TestEndpointRouteBuilder MapEndpoints(
        ServiceProvider provider,
        Action<AttentionEndpointsOptions>? configure = null)
    {
        var endpoints = new TestEndpointRouteBuilder(provider);
        endpoints.MapAttentionEndpoints<AttentionTestDbContext>("api/attention", configure);
        return endpoints;
    }

    /// <summary>Seeds one active indexed-mode signal for the given entity type and id.</summary>
    private static async Task SeedSignal(IServiceProvider scopedServices, string entityType, long entityId)
    {
        var db = scopedServices.GetRequiredService<AttentionTestDbContext>();

        db.Set<AttentionSignalEntry>().Add(new AttentionSignalEntry
        {
            EntityType = entityType,
            EntityId = entityId,
            Source = "Test",
            Category = "Test",
            Severity = AttentionSeverity.Warning,
            RaisedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync();
    }

    /// <summary>Seeds the widget row itself, so a clear that is allowed can really run.</summary>
    private static async Task SeedWidget(IServiceProvider scopedServices, long entityId)
    {
        var db = scopedServices.GetRequiredService<AttentionTestDbContext>();
        db.Widgets.Add(new WidgetEntity { ID = entityId, Name = "W", HasActiveAttention = true, ActiveSignalCount = 1 });
        await db.SaveChangesAsync();
    }

    /// <summary>Invokes a mapped endpoint's request delegate directly and reads the response back.</summary>
    private static async Task<(int Status, string Body)> Invoke(
        RouteEndpoint endpoint,
        IServiceProvider scopedServices,
        object? jsonBody = null)
    {
        var httpContext = new DefaultHttpContext { RequestServices = scopedServices };
        httpContext.Response.Body = new MemoryStream();

        if (jsonBody is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(jsonBody));
            httpContext.Request.Method = "POST";
            httpContext.Request.ContentType = "application/json";
            httpContext.Request.ContentLength = bytes.Length;
            httpContext.Request.Body = new MemoryStream(bytes);

            // Minimal-API body binding asks this feature whether the request can carry a body.
            // A real server provides it; DefaultHttpContext does not, and without it the body
            // is treated as absent (400 before the handler runs).
            httpContext.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyAllowed());
        }

        await endpoint.RequestDelegate!(httpContext);

        httpContext.Response.Body.Position = 0;
        var body = await new StreamReader(httpContext.Response.Body).ReadToEndAsync();
        return (httpContext.Response.StatusCode, body);
    }

    private sealed class BodyAllowed : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }

    private static object ClearRequest(string entityType, long entityId)
        => new { EntityType = entityType, EntityId = entityId.ToString() };

    // ── 1. The hook alone decides when set ──────────────────────────────────────

    [Fact]
    public async Task Hook_WhenSet_AloneDecides_ForGetActive()
    {
        // The hook denies WidgetEntity even though it is mapped and fully granted, and allows
        // GadgetEntity even though it is unmapped and the unmapped default is Deny. Both
        // outcomes prove the hook is the full override: registry and default are not consulted.
        using var provider = BuildProvider(WidgetActionMap(), WidgetsGrant(Access.Read, Access.Write));
        var endpoints = MapEndpoints(provider, o => o.AuthorizeEntityType =
            (_, entityType, _) => ValueTask.FromResult(entityType == nameof(GadgetEntity)));

        using var scope = provider.CreateScope();
        await SeedSignal(scope.ServiceProvider, nameof(WidgetEntity), 42);
        await SeedSignal(scope.ServiceProvider, nameof(GadgetEntity), 7);

        var (status, body) = await Invoke(endpoints.Endpoint("api/attention/active"), scope.ServiceProvider);

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Contains(nameof(GadgetEntity), body);
        Assert.DoesNotContain(nameof(WidgetEntity), body);
    }

    [Fact]
    public async Task Hook_WhenSet_AloneDecides_ForClear()
    {
        // Deny by hook → 403, even though the type is mapped and the caller has Write.
        using var provider = BuildProvider(WidgetActionMap(), WidgetsGrant(Access.Read, Access.Write));
        var endpoints = MapEndpoints(provider, o => o.AuthorizeEntityType =
            (_, _, access) => ValueTask.FromResult(access != AttentionEndpointAccess.Clear));

        using var scope = provider.CreateScope();

        var (status, _) = await Invoke(
            endpoints.Endpoint("api/attention/clear"),
            scope.ServiceProvider,
            ClearRequest(nameof(WidgetEntity), 42));

        Assert.Equal(StatusCodes.Status403Forbidden, status);
    }

    [Fact]
    public async Task Hook_Allowing_LetsAnUnmappedTypeThrough_DespiteTheDenyDefault()
    {
        // Allow by hook → the clear really runs (200), even though the type is unmapped and
        // UnmappedEntityTypeAccess stays at the Deny default.
        using var provider = BuildProvider();
        var endpoints = MapEndpoints(provider, o => o.AuthorizeEntityType =
            (_, _, _) => ValueTask.FromResult(true));

        using var scope = provider.CreateScope();
        await SeedWidget(scope.ServiceProvider, 42);
        await SeedSignal(scope.ServiceProvider, nameof(WidgetEntity), 42);

        var (status, _) = await Invoke(
            endpoints.Endpoint("api/attention/clear"),
            scope.ServiceProvider,
            ClearRequest(nameof(WidgetEntity), 42));

        Assert.Equal(StatusCodes.Status200OK, status);
    }

    // ── 2. Mapped types: the registry action gates access via TypeAuth ─────────

    [Fact]
    public async Task MappedType_WithReadGrant_IsIncludedInGetActive()
    {
        using var provider = BuildProvider(WidgetActionMap(), WidgetsGrant(Access.Read));
        var endpoints = MapEndpoints(provider);

        using var scope = provider.CreateScope();
        await SeedSignal(scope.ServiceProvider, nameof(WidgetEntity), 42);

        var (status, body) = await Invoke(endpoints.Endpoint("api/attention/active"), scope.ServiceProvider);

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Contains(nameof(WidgetEntity), body);
    }

    [Fact]
    public async Task MappedType_WithoutReadGrant_IsLeftOutOfGetActive()
    {
        // The caller only holds Write — reading the type's signals requires CanRead.
        using var provider = BuildProvider(WidgetActionMap(), WidgetsGrant(Access.Write));
        var endpoints = MapEndpoints(provider);

        using var scope = provider.CreateScope();
        await SeedSignal(scope.ServiceProvider, nameof(WidgetEntity), 42);

        var (status, body) = await Invoke(endpoints.Endpoint("api/attention/active"), scope.ServiceProvider);

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.DoesNotContain(nameof(WidgetEntity), body);
    }

    [Fact]
    public async Task MappedType_WithWriteGrant_CanClear()
    {
        // Clearing is gated on CanWrite — the same check the entity's own secure endpoints
        // apply to a save.
        using var provider = BuildProvider(WidgetActionMap(), WidgetsGrant(Access.Read, Access.Write));
        var endpoints = MapEndpoints(provider);

        using var scope = provider.CreateScope();
        await SeedWidget(scope.ServiceProvider, 42);
        await SeedSignal(scope.ServiceProvider, nameof(WidgetEntity), 42);

        var (status, _) = await Invoke(
            endpoints.Endpoint("api/attention/clear"),
            scope.ServiceProvider,
            ClearRequest(nameof(WidgetEntity), 42));

        Assert.Equal(StatusCodes.Status200OK, status);

        // The clear really ran: the seeded signal is no longer active.
        var db = scope.ServiceProvider.GetRequiredService<AttentionTestDbContext>();
        Assert.DoesNotContain(
            await db.Set<AttentionSignalEntry>().ToListAsync(TestContext.Current.CancellationToken),
            x => x.ClearedAt == null);
    }

    [Fact]
    public async Task MappedType_WithOnlyReadGrant_GetsA403OnClear()
    {
        using var provider = BuildProvider(WidgetActionMap(), WidgetsGrant(Access.Read));
        var endpoints = MapEndpoints(provider);

        using var scope = provider.CreateScope();

        var (status, _) = await Invoke(
            endpoints.Endpoint("api/attention/clear"),
            scope.ServiceProvider,
            ClearRequest(nameof(WidgetEntity), 42));

        Assert.Equal(StatusCodes.Status403Forbidden, status);
    }

    // ── 3. Unmapped types with the Deny default ─────────────────────────────────

    [Fact]
    public async Task UnmappedType_ByDefault_IsLeftOutOfGetActive()
    {
        // No hook, no registry entry, options untouched: the Deny default hides the signals.
        // No action map is registered at all here, which must behave like an empty map.
        using var provider = BuildProvider();
        var endpoints = MapEndpoints(provider);

        using var scope = provider.CreateScope();
        await SeedSignal(scope.ServiceProvider, nameof(WidgetEntity), 42);

        var (status, body) = await Invoke(endpoints.Endpoint("api/attention/active"), scope.ServiceProvider);

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.DoesNotContain(nameof(WidgetEntity), body);
    }

    [Fact]
    public async Task UnmappedType_ByDefault_GetsA403OnClear()
    {
        using var provider = BuildProvider();
        var endpoints = MapEndpoints(provider);

        using var scope = provider.CreateScope();

        var (status, _) = await Invoke(
            endpoints.Endpoint("api/attention/clear"),
            scope.ServiceProvider,
            ClearRequest(nameof(WidgetEntity), 42));

        Assert.Equal(StatusCodes.Status403Forbidden, status);
    }

    [Fact]
    public async Task UnmappedType_IsDenied_EvenWhenOtherTypesAreMapped()
    {
        // A registered map with an entry for one type does not open access to other types:
        // the mapped type passes its TypeAuth check, the unmapped type is denied by the default.
        using var provider = BuildProvider(WidgetActionMap(), WidgetsGrant(Access.Read));
        var endpoints = MapEndpoints(provider);

        using var scope = provider.CreateScope();
        await SeedSignal(scope.ServiceProvider, nameof(WidgetEntity), 42);
        await SeedSignal(scope.ServiceProvider, nameof(GadgetEntity), 7);

        var (status, body) = await Invoke(endpoints.Endpoint("api/attention/active"), scope.ServiceProvider);

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Contains(nameof(WidgetEntity), body);
        Assert.DoesNotContain(nameof(GadgetEntity), body);
    }

    // ── 4. Unmapped types with AllowAuthenticated ───────────────────────────────

    [Fact]
    public async Task UnmappedType_WithAllowAuthenticated_IsIncludedInGetActive()
    {
        // The pre-registry behavior, now opt-in: any authenticated caller may read.
        using var provider = BuildProvider();
        var endpoints = MapEndpoints(provider, o =>
            o.UnmappedEntityTypeAccess = AttentionUnmappedEntityTypeAccess.AllowAuthenticated);

        using var scope = provider.CreateScope();
        await SeedSignal(scope.ServiceProvider, nameof(WidgetEntity), 42);

        var (status, body) = await Invoke(endpoints.Endpoint("api/attention/active"), scope.ServiceProvider);

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Contains(nameof(WidgetEntity), body);
    }

    [Fact]
    public async Task UnmappedType_WithAllowAuthenticated_CanClear()
    {
        using var provider = BuildProvider();
        var endpoints = MapEndpoints(provider, o =>
            o.UnmappedEntityTypeAccess = AttentionUnmappedEntityTypeAccess.AllowAuthenticated);

        using var scope = provider.CreateScope();
        await SeedWidget(scope.ServiceProvider, 42);
        await SeedSignal(scope.ServiceProvider, nameof(WidgetEntity), 42);

        var (status, _) = await Invoke(
            endpoints.Endpoint("api/attention/clear"),
            scope.ServiceProvider,
            ClearRequest(nameof(WidgetEntity), 42));

        Assert.Equal(StatusCodes.Status200OK, status);
    }

    [Fact]
    public async Task AllowAuthenticated_DoesNotBypass_AMappedTypesTypeAuthCheck()
    {
        // AllowAuthenticated only applies to unmapped types. A mapped type is still gated on
        // its registered action.
        using var provider = BuildProvider(WidgetActionMap(), WidgetsGrant(/* nothing granted */));
        var endpoints = MapEndpoints(provider, o =>
            o.UnmappedEntityTypeAccess = AttentionUnmappedEntityTypeAccess.AllowAuthenticated);

        using var scope = provider.CreateScope();
        await SeedSignal(scope.ServiceProvider, nameof(WidgetEntity), 42);

        var (status, body) = await Invoke(endpoints.Endpoint("api/attention/active"), scope.ServiceProvider);

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.DoesNotContain(nameof(WidgetEntity), body);
    }
}
