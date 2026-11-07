using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Notifications;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.EFCore.Notifications;

namespace Microsoft.Extensions.DependencyInjection;

public static class NotificationServiceCollectionExtensions
{
    /// <summary>
    /// Opts this host in to notifications and registers its topics, the raiser, the queue and the background service. A
    /// host that doesn't call this behaves exactly as before: nothing from this feature is added to its services.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <paramref name="systemName"/> is the host's own name, and every topic ID must start with it. A wrong topic throws
    /// here, so the host fails at startup and not when the first event is raised.
    /// </para>
    /// <para>
    /// <typeparamref name="TDbContext"/> is the context the host's saves run on. An event waits for that context's commit,
    /// so this also adds <c>UseShiftEntityAfterCommit()</c> to it. A pooled context is not supported, as with replication.
    /// </para>
    /// <para>
    /// Calling this again with the same system name and context adds more topics, so a host can register them from
    /// several places. Calling it with a different name or context throws.
    /// </para>
    /// <para>
    /// The host also registers its <see cref="INotificationEmailSender"/>, and an
    /// <see cref="INotificationEmailContent{TEntity}"/> per entity type. ShiftIdentity's
    /// <c>AddIdentityNotificationDirectory</c> is what lets events reach users, teams and branch tags.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddNotifications<TDbContext>(
        this IServiceCollection services,
        string systemName,
        Action<NotificationOptions> configure)
        where TDbContext : ShiftDbContext
    {
        ArgumentNullException.ThrowIfNull(configure);

        var registration = GetOrAddRegistration<TDbContext>(services, systemName);

        configure(registration.Options);

        foreach (var topic in registration.Options.TakeAddedTopics())
            registration.Catalog.Add(topic);

        return services;
    }

    /// <summary>
    /// Reads the subscribers list from a JSON file that ships with the host, usually an embedded resource. The top-level
    /// keys are section names, normally environment names, and each holds an array of entries. It is read once, when the
    /// host starts. Every change therefore goes through review and a deploy.
    /// </summary>
    /// <remarks>
    /// A mistake in the file never stops the host, because a typo in the list mustn't stop saves. An invalid entry is
    /// dropped and logged as an error, and the valid ones still load. A file that can't be read gives an empty list and an
    /// error. A missing section gives an empty list and a warning. A test in the host should prove that the file has no
    /// invalid entries: <see cref="NotificationSubscribersJson"/> reads it the same way.
    /// </remarks>
    /// <param name="services">The host's services.</param>
    /// <param name="openJson">Opens the file. The call owns the stream and closes it. Null counts as a missing file.</param>
    /// <param name="section">The section to use, such as this host's environment name.</param>
    public static IServiceCollection AddNotificationSubscribersJson(
        this IServiceCollection services,
        Func<Stream?> openJson,
        string? section)
    {
        ArgumentNullException.ThrowIfNull(openJson);

        services.AddSingleton(sp => new JsonNotificationSubscribersList(
            openJson,
            section,
            sp.GetRequiredService<NotificationSubscribersEntryValidator>(),
            sp.GetRequiredService<ILogger<JsonNotificationSubscribersList>>()));

        // Takes the place of the in-memory list, in whichever order the two registrations run.
        services.RemoveAll<INotificationSubscribersList>();
        services.AddSingleton<INotificationSubscribersList>(sp => sp.GetRequiredService<JsonNotificationSubscribersList>());

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, NotificationSubscribersJsonLoader>());

        return services;
    }

    private static NotificationRegistration GetOrAddRegistration<TDbContext>(IServiceCollection services, string systemName)
        where TDbContext : ShiftDbContext
    {
        var existing = services.FirstOrDefault(d => d.ServiceType == typeof(NotificationRegistration));

        if (existing?.ImplementationInstance is NotificationRegistration registration)
        {
            if (registration.Catalog.SystemName != systemName)
                throw new InvalidOperationException(
                    $"This host already registered notifications as '{registration.Catalog.SystemName}', so it can't also register them as '{systemName}'.");

            if (registration.DbContextType != typeof(TDbContext))
                throw new InvalidOperationException(
                    $"This host already registered notifications on {registration.DbContextType.Name}, so it can't also register them on {typeof(TDbContext).Name}.");

            return registration;
        }

        // The instances are registered as they are, so that every call adds to the same ones, and a wrong topic fails here,
        // before the service provider is built.
        var created = new NotificationRegistration(new NotificationTopicCatalog(systemName), new NotificationOptions(), typeof(TDbContext));

        services.AddSingleton(created);
        services.AddSingleton(created.Catalog);
        services.AddSingleton(created.Options);

        services.ConfigureDbContext<TDbContext>((_, options) => options.UseShiftEntityAfterCommit(), ServiceLifetime.Singleton);

        services.AddSingleton<NotificationQueue>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, NotificationDispatcherService>());

        services.TryAddSingleton<NotificationWhereMatcher>();
        services.TryAddSingleton<NotificationSubscribersEntryValidator>();
        services.TryAddSingleton<INotificationSubscribersList, InMemoryNotificationSubscribersList>();

        services.AddScoped<INotificationRaiser>(sp => new NotificationRaiser(
            sp.GetRequiredService<TDbContext>(),
            sp.GetRequiredService<NotificationTopicCatalog>(),
            sp.GetRequiredService<NotificationQueue>(),
            sp.GetRequiredService<ILogger<NotificationRaiser>>()));

        services.AddScoped(sp => new NotificationPipeline(
            sp,
            sp.GetRequiredService<TDbContext>(),
            sp.GetRequiredService<NotificationTopicCatalog>(),
            sp.GetRequiredService<NotificationOptions>(),
            sp.GetRequiredService<NotificationWhereMatcher>(),
            sp.GetRequiredService<IHashIdService>(),
            sp.GetRequiredService<ILogger<NotificationPipeline>>()));

        return created;
    }

    private sealed record NotificationRegistration(NotificationTopicCatalog Catalog, NotificationOptions Options, Type DbContextType);
}
