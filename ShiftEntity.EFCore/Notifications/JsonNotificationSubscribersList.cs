using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ShiftSoftware.ShiftEntity.Core.Notifications;
using ShiftSoftware.ShiftEntity.Model.Notifications;

namespace ShiftSoftware.ShiftEntity.EFCore.Notifications;

/// <summary>
/// The subscribers list from a JSON file that ships with the host. It is read once. Nothing in the file can stop the host,
/// because a typo in the list mustn't stop saves.
/// </summary>
internal sealed class JsonNotificationSubscribersList : INotificationSubscribersList
{
    private readonly IReadOnlyList<NotificationSubscribersEntryDTO> entries;

    public JsonNotificationSubscribersList(
        Func<Stream?> openJson,
        string? section,
        NotificationSubscribersEntryValidator validator,
        ILogger<JsonNotificationSubscribersList> logger)
    {
        entries = Load(openJson, section, validator, logger);
    }

    public Task<IReadOnlyList<NotificationSubscribersEntryDTO>> GetEntriesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(entries);

    private static IReadOnlyList<NotificationSubscribersEntryDTO> Load(
        Func<Stream?> openJson,
        string? section,
        NotificationSubscribersEntryValidator validator,
        ILogger logger)
    {
        try
        {
            using var json = openJson();

            if (json is null)
            {
                logger.LogError("The notification subscribers file wasn't found, so the list is empty.");
                return [];
            }

            var result = NotificationSubscribersJson.ReadSection(json, section, validator);

            foreach (var error in result.Errors)
                logger.LogError("The notification subscribers file has a problem, and that part is dropped. {Error}", error);

            if (!result.Found && result.Errors.Count == 0)
                logger.LogWarning("The notification subscribers file has no section '{Section}', so the list is empty.", section);
            else
                logger.LogInformation("The notification subscribers list has {Count} entries from section '{Section}'.", result.Entries.Count, section);

            return result.Entries;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "The notification subscribers file couldn't be read, so the list is empty.");
            return [];
        }
    }
}

/// <summary>
/// Reads the subscribers file when the host starts, so that a mistake in it shows in the log at once and not when the
/// first event is raised.
/// </summary>
internal sealed class NotificationSubscribersJsonLoader : IHostedService
{
    private readonly IServiceProvider services;
    private readonly ILogger<NotificationSubscribersJsonLoader> logger;

    public NotificationSubscribersJsonLoader(IServiceProvider services, ILogger<NotificationSubscribersJsonLoader> logger)
    {
        this.services = services;
        this.logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            services.GetRequiredService<JsonNotificationSubscribersList>();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "The notification subscribers list couldn't be set up. Events will fail and be logged until this is fixed.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
