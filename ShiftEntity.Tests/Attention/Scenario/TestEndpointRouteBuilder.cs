using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace ShiftSoftware.ShiftEntity.Tests.Attention.Scenario;

/// <summary>
/// A minimal <see cref="IEndpointRouteBuilder"/> for tests that map endpoints without a web
/// host. The framework's map extensions add their endpoints to <see cref="DataSources"/>;
/// a test can then read the mapped <see cref="RouteEndpoint"/>s back and invoke their request
/// delegates directly against a hand-built <c>HttpContext</c>.
/// </summary>
public sealed class TestEndpointRouteBuilder : IEndpointRouteBuilder
{
    public TestEndpointRouteBuilder(IServiceProvider serviceProvider)
    {
        ServiceProvider = serviceProvider;
    }

    public IServiceProvider ServiceProvider { get; }

    public ICollection<EndpointDataSource> DataSources { get; } = new List<EndpointDataSource>();

    public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);

    /// <summary>All endpoints mapped so far, flattened across data sources.</summary>
    public IReadOnlyList<RouteEndpoint> Endpoints
        => DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().ToList();

    /// <summary>The single endpoint whose route pattern is exactly <paramref name="routePattern"/>.</summary>
    public RouteEndpoint Endpoint(string routePattern)
        => Endpoints.Single(e => string.Equals(e.RoutePattern.RawText, routePattern, StringComparison.OrdinalIgnoreCase));
}
