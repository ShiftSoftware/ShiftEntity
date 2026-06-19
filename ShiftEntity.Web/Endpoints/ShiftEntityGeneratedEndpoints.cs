using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.Web.Endpoints;
using ShiftSoftware.TypeAuth.Core.Actions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ShiftSoftware.ShiftEntity.Web.Endpoints;

/// <summary>A discovered endpoint spec paired with the DB it should bind its built-in repository to.</summary>
internal readonly record struct ShiftEntityEndpointEntry(ShiftEntityEndpointSpec Spec, Type DbType);

/// <summary>
/// Accumulates the endpoint entries registered across one or more <c>AddShiftEntityEndpoints&lt;DB&gt;()</c>
/// calls so a single data source serves them all (avoids duplicate route registration). Routes are
/// deduplicated as they're added.
/// </summary>
internal sealed class ShiftEntityGeneratedEndpointRegistry
{
    private readonly HashSet<string> routes = new(StringComparer.OrdinalIgnoreCase);
    public List<ShiftEntityEndpointEntry> Entries { get; } = new();

    public void Add(ShiftEntityEndpointSpec spec, Type dbType)
    {
        // Skip a route already registered (e.g. AddShiftEntityEndpoints called twice) — adding it again
        // would produce a duplicate endpoint and an AmbiguousMatchException at request time.
        if (routes.Add("/" + spec.Route.Trim().Trim('/').ToLowerInvariant()))
            Entries.Add(new ShiftEntityEndpointEntry(spec, dbType));
    }
}

/// <summary>
/// Generates minimal-API CRUD endpoints from entity endpoint attributes (see
/// <see cref="ShiftEntityEndpointDiscovery"/>) by invoking the existing
/// <see cref="ShiftEntityEndpointRouteBuilderExtensions"/> map methods via reflection — no endpoint
/// logic is duplicated.
/// </summary>
internal static class ShiftEntityGeneratedEndpoints
{
    // The no-configure overloads: (IEndpointRouteBuilder, string) and (IEndpointRouteBuilder, string, ReadWriteDeleteAction).
    private static readonly MethodInfo CrudMethod = ResolveMap(nameof(ShiftEntityEndpointRouteBuilderExtensions.MapShiftEntityCrud), secure: false);
    private static readonly MethodInfo SecureMethod = ResolveMap(nameof(ShiftEntityEndpointRouteBuilderExtensions.MapShiftEntitySecureCrud), secure: true);

    internal static void Generate(IEndpointRouteBuilder routeBuilder, IEnumerable<ShiftEntityEndpointEntry> entries)
    {
        foreach (var (spec, dbType) in entries)
        {
            var repositoryType = ResolveRepositoryType(spec, dbType);
            ValidateRepository(repositoryType, spec);

            if (spec.Secure)
            {
                var action = ResolveAction(spec.ActionTreeType!, spec.ActionName!);
                SecureMethod.MakeGenericMethod(repositoryType, spec.Entity, spec.ListDto, spec.ViewDto)
                    .Invoke(null, new object?[] { routeBuilder, spec.Route, action });
            }
            else
            {
                CrudMethod.MakeGenericMethod(repositoryType, spec.Entity, spec.ListDto, spec.ViewDto)
                    .Invoke(null, new object?[] { routeBuilder, spec.Route });
            }
        }
    }

    // Custom repository if the attribute named one; otherwise the framework's built-in
    // ShiftRepository<DB, Entity, ListDTO, ViewDTO> closed over the app's concrete DB.
    internal static Type ResolveRepositoryType(ShiftEntityEndpointSpec spec, Type dbType)
        => spec.Repository ?? typeof(ShiftRepository<,,,>).MakeGenericType(dbType, spec.Entity, spec.ListDto, spec.ViewDto);

    // The map methods require Repository : IShiftRepositoryAsync<Entity, ListDTO, ViewDTO>; the attribute
    // only constrains TRepository : ShiftRepositoryBase. Validate here so a mismatched custom repository
    // fails with a clear message instead of an opaque MakeGenericMethod ArgumentException.
    private static void ValidateRepository(Type repositoryType, ShiftEntityEndpointSpec spec)
    {
        var required = typeof(IShiftRepositoryAsync<,,>).MakeGenericType(spec.Entity, spec.ListDto, spec.ViewDto);

        if (!required.IsAssignableFrom(repositoryType))
            throw new InvalidOperationException(
                $"Repository '{repositoryType.FullName}' for the endpoint '{spec.Route}' on entity '{spec.Entity.FullName}' " +
                $"must implement {required.FullName}. A custom repository must be a " +
                $"ShiftRepository<DB, {spec.Entity.Name}, {spec.ListDto.Name}, {spec.ViewDto.Name}> (or a subclass).");
    }

    private static ReadWriteDeleteAction ResolveAction(Type actionTreeType, string actionName)
    {
        var field = actionTreeType
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(f => string.Equals(f.Name, actionName, StringComparison.OrdinalIgnoreCase));

        if (field is null)
            throw new InvalidOperationException(
                $"Action '{actionName}' was not found as a public static field on '{actionTreeType.FullName}'. " +
                "The TActionTree of a secure endpoint attribute must directly declare the named action field.");

        if (field.GetValue(null) is not ReadWriteDeleteAction action)
            throw new InvalidOperationException(
                $"Action '{actionName}' on '{actionTreeType.FullName}' is not a {nameof(ReadWriteDeleteAction)}. " +
                "Secure endpoints can only bind to a ReadWriteDeleteAction node (not a dynamic / data-level action).");

        return action;
    }

    private static MethodInfo ResolveMap(string name, bool secure)
    {
        var expectedLength = secure ? 3 : 2;

        foreach (var m in typeof(ShiftEntityEndpointRouteBuilderExtensions)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == name && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 4))
        {
            var p = m.GetParameters();
            if (p.Length != expectedLength) continue;
            if (p[0].ParameterType != typeof(IEndpointRouteBuilder)) continue;
            if (p[1].ParameterType != typeof(string)) continue;
            if (secure && p[2].ParameterType != typeof(ReadWriteDeleteAction)) continue;
            return m;
        }

        throw new InvalidOperationException($"Could not resolve the no-configure overload of {name}.");
    }
}

/// <summary>
/// A DI-registered <see cref="EndpointDataSource"/> that lazily generates the attribute-driven CRUD
/// endpoints on first enumeration (after the application's service provider exists).
///
/// Note: <see cref="WebApplication"/> does NOT auto-include DI-registered endpoint data sources, so
/// the accompanying <see cref="ShiftEntityGeneratedEndpointsStartupFilter"/> is what actually adds this
/// source to the application's endpoint route builder — do not remove it.
/// </summary>
internal sealed class ShiftEntityGeneratedEndpointDataSource : EndpointDataSource
{
    private readonly IServiceProvider serviceProvider;
    private readonly ShiftEntityGeneratedEndpointRegistry registry;
    private readonly object gate = new();
    private volatile IReadOnlyList<Endpoint>? endpoints;

    public ShiftEntityGeneratedEndpointDataSource(IServiceProvider serviceProvider, ShiftEntityGeneratedEndpointRegistry registry)
    {
        this.serviceProvider = serviceProvider;
        this.registry = registry;
    }

    public override IReadOnlyList<Endpoint> Endpoints
    {
        get
        {
            if (endpoints is not null)
                return endpoints;

            lock (gate)
            {
                if (endpoints is null)
                {
                    var routeBuilder = new InternalEndpointRouteBuilder(serviceProvider);
                    ShiftEntityGeneratedEndpoints.Generate(routeBuilder, registry.Entries);
                    endpoints = routeBuilder.DataSources.SelectMany(ds => ds.Endpoints).ToList();
                }

                return endpoints;
            }
        }
    }

    // The generated set never changes after first build, so a no-op token is correct.
    public override Microsoft.Extensions.Primitives.IChangeToken GetChangeToken() => NoOpChangeToken.Instance;

    private sealed class NoOpChangeToken : Microsoft.Extensions.Primitives.IChangeToken
    {
        public static readonly NoOpChangeToken Instance = new();
        public bool HasChanged => false;
        public bool ActiveChangeCallbacks => false;
        public IDisposable RegisterChangeCallback(Action<object?> callback, object? state) => NoOpDisposable.Instance;

        private sealed class NoOpDisposable : IDisposable
        {
            public static readonly NoOpDisposable Instance = new();
            public void Dispose() { }
        }
    }
}

/// <summary>
/// Adds the generated <see cref="ShiftEntityGeneratedEndpointDataSource"/> to the application's endpoint
/// route builder so the routes are served without the programmer calling <c>app.Map…</c>.
/// </summary>
internal sealed class ShiftEntityGeneratedEndpointsStartupFilter : IStartupFilter
{
    // The IEndpointRouteBuilder UseRouting stores (and never removes) under this key. We add our data
    // source AFTER the inner pipeline runs, so the app's UseRouting/UseEndpoints (which it wires when it
    // has any endpoints) have already populated it; our source then rides the same live composite.
    private const string EndpointRouteBuilderKey = "__EndpointRouteBuilder";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        next(app);

        var dataSources = app.ApplicationServices.GetServices<EndpointDataSource>()
            .OfType<ShiftEntityGeneratedEndpointDataSource>()
            .ToList();

        if (dataSources.Count == 0)
            return;

        if (app.Properties.TryGetValue(EndpointRouteBuilderKey, out var value) && value is IEndpointRouteBuilder routeBuilder)
        {
            foreach (var dataSource in dataSources)
                if (!routeBuilder.DataSources.Contains(dataSource))
                    routeBuilder.DataSources.Add(dataSource);

            return;
        }

        // No endpoint routing was wired by the app. WebApplication only enables UseRouting/UseEndpoints
        // when it already has endpoints, so an app whose ONLY endpoints are these generated ones would
        // silently 404. We can't safely add routing here (it would run after the app's auth middleware,
        // risking an authorization bypass on secure endpoints), so fail fast with actionable guidance.
        throw new InvalidOperationException(
            "ShiftEntity could not auto-map the attribute-driven endpoints because the application has no " +
            "other endpoints, so ASP.NET Core did not enable endpoint routing. Enable routing — e.g. map at " +
            "least one other endpoint (app.MapControllers(), …) or call app.UseRouting()/app.UseEndpoints().");
    };
}

/// <summary>
/// A minimal <see cref="IEndpointRouteBuilder"/> used to host the generated endpoints inside the data
/// source above (the map methods write their route data sources into <see cref="DataSources"/>).
/// </summary>
internal sealed class InternalEndpointRouteBuilder : IEndpointRouteBuilder
{
    public InternalEndpointRouteBuilder(IServiceProvider serviceProvider)
    {
        ServiceProvider = serviceProvider;
    }

    public IServiceProvider ServiceProvider { get; }

    public ICollection<EndpointDataSource> DataSources { get; } = new List<EndpointDataSource>();

    public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);
}
