using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Reflection;

namespace ShiftSoftware.ShiftEntity.EFCore;

/// <summary>
/// What tells a host that it still uses the trigger library (EntityFrameworkCore.Triggered). The framework no longer
/// references that library and no longer switches it on, so a trigger that is still registered would simply stop
/// running. A host with triggers is stopped instead, with a message that says how to convert them.
/// </summary>
/// <remarks>
/// Nothing here references the library: a type belongs to it when its assembly's name starts with the library's.
/// </remarks>
internal static class TriggerLibrary
{
    public const string AssemblyName = "EntityFrameworkCore.Triggered";

    private static readonly ConcurrentDictionary<Assembly, bool> assemblies = new();

    /// <summary>Whether a type comes from one of the library's assemblies. A closed generic type is its definition's.</summary>
    public static bool Owns(Type? type) => type is not null && assemblies.GetOrAdd(type.Assembly,
        static assembly => assembly.GetName().Name?.StartsWith(AssemblyName, StringComparison.OrdinalIgnoreCase) == true);

    public const string HowToConvert =
        "Convert each trigger to a save handler. A before-save trigger (IBeforeSaveTrigger<T>) becomes an IBeforeSaveHandler<T>. " +
        "An after-save trigger (IAfterSaveTrigger<T>) becomes an IAfterCommitHandler<T> when it tells another system, or an " +
        "IAfterSaveHandler<T> when it writes something that belongs to the save. Declare each handler with the context " +
        "(ConfigureSaveHandlers) or with the host (services.AddSaveHandler<THandler>()). Then remove UseTriggers and the " +
        "package reference.";

    /// <summary>Throws when a context's options carry an extension of the library: <c>UseTriggers</c> was called for it.</summary>
    public static void Check(IDbContextOptions options)
    {
        foreach (var extension in options.Extensions)
        {
            if (!Owns(extension.GetType()))
                continue;

            var context = (options as DbContextOptions)?.ContextType.Name ?? "A ShiftDbContext";

            throw new TriggersNotConvertedException(
                $"{context} has EntityFrameworkCore.Triggered switched on: UseTriggers is called in its OnConfiguring or where the context " +
                $"is registered. The framework's save lifecycle has taken the place of that library and does not work beside it. {HowToConvert}");
        }
    }
}

/// <summary>A host or a context still uses the trigger library.</summary>
internal sealed class TriggersNotConvertedException(string message) : InvalidOperationException(message);

/// <summary>
/// Rides in the options of every <see cref="ShiftDbContext"/>. EF validates an options extension the first time it
/// builds its services for a given set of options, and with the final options, after <c>OnConfiguring</c>. That is
/// where a context finds out that the trigger library is switched on for it.
/// </summary>
internal sealed class SaveLifecycleOptionsExtension : IDbContextOptionsExtension
{
    public static readonly SaveLifecycleOptionsExtension Instance = new();

    private SaveLifecycleOptionsExtension() => Info = new ExtensionInfo(this);

    public DbContextOptionsExtensionInfo Info { get; }

    public void ApplyServices(IServiceCollection services) { }

    public void Validate(IDbContextOptions options) => TriggerLibrary.Check(options);

    /// <summary>The options with the extension in them. A context is constructed with these.</summary>
    public static DbContextOptions AddTo(DbContextOptions options) =>
        options is null || options.FindExtension<SaveLifecycleOptionsExtension>() is not null ? options! : options.WithExtension(Instance);

    private sealed class ExtensionInfo(IDbContextOptionsExtension extension) : DbContextOptionsExtensionInfo(extension)
    {
        public override bool IsDatabaseProvider => false;
        public override string LogFragment => "";
        public override int GetServiceProviderHashCode() => 0;
        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) => other is ExtensionInfo;
        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo) { }
    }
}

/// <summary>
/// What a host's registrations say about its save lifecycle, kept for the life of the host: the services it was built
/// from. A handler's lifetime is read from them, and the check for the trigger library scans them when the host starts.
/// </summary>
internal sealed class SaveLifecycleHost(IServiceCollection services)
{
    private readonly ConcurrentDictionary<Type, ServiceLifetime?> lifetimes = new();

    public IServiceCollection Services { get; } = services;

    /// <summary>How the host registered a class in its services, or null when it did not. The last registration counts.</summary>
    public ServiceLifetime? LifetimeOf(Type serviceType) => lifetimes.GetOrAdd(serviceType, type =>
    {
        var definition = type.IsGenericType && !type.IsGenericTypeDefinition ? type.GetGenericTypeDefinition() : null;
        ServiceLifetime? open = null;

        for (var i = Services.Count - 1; i >= 0; i--)
        {
            var descriptor = Services[i];

            if (descriptor.IsKeyedService)
                continue;

            if (descriptor.ServiceType == type)
                return descriptor.Lifetime;

            // A closed generic class may be registered as its open definition.
            if (open is null && definition is not null && descriptor.ServiceType == definition)
                open = descriptor.Lifetime;
        }

        return open;
    });
}

/// <summary>
/// Stops a host that still has triggers. When the host starts it scans the registrations for a service that belongs to
/// the trigger library, such as a trigger registered in DI, and creates each registered <see cref="ShiftDbContext"/>
/// once, in a scope of its own, so that a context with the library switched on in its options says so before the host
/// serves anything. Creating a context opens no connection.
/// </summary>
/// <remarks>
/// Every registration of the framework that a host of saves goes through adds it, once: <c>AddShiftEntityWeb</c> and
/// <c>AddShiftEntityFunctions</c>, which every API and every functions host calls, both replication registrations,
/// <c>AddNotifications</c> and <c>AddSaveHandler</c>. It runs wherever a generic host starts its hosted services. A
/// context built without a host makes the check for its own options when it is first created with them.
/// </remarks>
internal sealed class TriggerLibraryCheck(SaveLifecycleHost host, IServiceScopeFactory scopes, IServiceProvider services) : IHostedService
{
    // A host without logging is checked all the same.
    private readonly ILogger? logger = services.GetService<ILoggerFactory>()?.CreateLogger<TriggerLibraryCheck>();

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var registered = new List<string>();
        var contextTypes = new List<Type>();

        foreach (var descriptor in host.Services)
        {
            var service = descriptor.ServiceType;

            var implementation = descriptor.IsKeyedService
                ? descriptor.KeyedImplementationType ?? descriptor.KeyedImplementationInstance?.GetType()
                : descriptor.ImplementationType ?? descriptor.ImplementationInstance?.GetType();

            if (TriggerLibrary.Owns(service) || TriggerLibrary.Owns(implementation))
            {
                var name = implementation is null || implementation == service ? Name(service) : $"{Name(implementation)} as {Name(service)}";

                if (!registered.Contains(name))
                    registered.Add(name);
            }

            if (!descriptor.IsKeyedService && typeof(ShiftDbContext).IsAssignableFrom(service) && !service.ContainsGenericParameters && !contextTypes.Contains(service))
                contextTypes.Add(service);
        }

        var switchedOn = new List<string>();

        if (contextTypes.Count > 0)
        {
            await using var scope = scopes.CreateAsyncScope();

            foreach (var contextType in contextTypes)
            {
                try
                {
                    if (scope.ServiceProvider.GetService(contextType) is ShiftDbContext context)
                        context.CheckForTheTriggerLibrary();
                }
                catch (TriggersNotConvertedException)
                {
                    switchedOn.Add(contextType.Name);
                }
                catch (Exception exception)
                {
                    // The context cannot be created here, for example because its options need something only a request
                    // has. It makes the check itself when it is first created.
                    logger?.LogDebug(exception, "{Context} could not be created when the host started, to check that the trigger library " +
                        "is not switched on for it. The context checks its own options when it is first created.", contextType.Name);
                }
            }
        }

        if (registered.Count == 0 && switchedOn.Count == 0)
            return;

        var found = new List<string>();

        if (registered.Count > 0)
            found.Add($"Registered in its services: {string.Join(", ", registered)}.");

        if (switchedOn.Count > 0)
            found.Add($"Switched on with UseTriggers for: {string.Join(", ", switchedOn)}.");

        throw new TriggersNotConvertedException(
            "This host still uses EntityFrameworkCore.Triggered. The framework no longer includes that library and no longer switches it " +
            $"on, so a trigger that is still registered would stop running without a sign. {string.Join(" ", found)} {TriggerLibrary.HowToConvert}");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static string Name(Type type)
    {
        if (!type.IsGenericType)
            return type.Name;

        var name = type.Name;
        var tick = name.IndexOf('`');

        if (tick >= 0)
            name = name[..tick];

        return type.IsGenericTypeDefinition
            ? $"{name}<>"
            : $"{name}<{string.Join(", ", type.GetGenericArguments().Select(Name))}>";
    }
}

internal static class SaveLifecycleHostServiceCollectionExtensions
{
    /// <summary>
    /// Adds what every host that takes part in the save lifecycle carries: its registrations, for the lifetimes of its
    /// handlers, and the check that stops a host that still has triggers. Safe to call from every registration method.
    /// </summary>
    public static IServiceCollection AddSaveLifecycleHost(this IServiceCollection services)
    {
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(SaveLifecycleHost)))
            services.AddSingleton(new SaveLifecycleHost(services));

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, TriggerLibraryCheck>());

        return services;
    }
}
