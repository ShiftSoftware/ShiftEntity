using Microsoft.Extensions.Logging;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Notifications;
using System.Globalization;

namespace ShiftSoftware.ShiftEntity.EFCore.Notifications;

/// <inheritdoc cref="INotificationRaiser"/>
internal sealed class NotificationRaiser : INotificationRaiser
{
    private readonly ShiftDbContext db;
    private readonly NotificationTopicCatalog catalog;
    private readonly NotificationQueue queue;
    private readonly ILogger<NotificationRaiser> logger;

    public NotificationRaiser(ShiftDbContext db, NotificationTopicCatalog catalog, NotificationQueue queue, ILogger<NotificationRaiser> logger)
    {
        this.db = db;
        this.catalog = catalog;
        this.queue = queue;
        this.logger = logger;
    }

    public Task RaiseAsync<TEntity>(NotificationTopic topic, TEntity record, IEnumerable<long>? except = null)
        where TEntity : ShiftEntity<TEntity>, new()
    {
        // A raise runs inside the host's trigger or hook, so an exception here would fail the save.
        try
        {
            var raised = Capture(topic, record, except);

            if (raised is not null)
                db.AfterCommit(() => queue.Enqueue(raised));
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not raise notification topic {Topic} on {EntityType}. The save isn't affected.",
                topic?.ID, typeof(TEntity).Name);
        }

        return Task.CompletedTask;
    }

    private NotificationRaised<TEntity>? Capture<TEntity>(NotificationTopic topic, TEntity record, IEnumerable<long>? except)
        where TEntity : ShiftEntity<TEntity>, new()
    {
        if (!catalog.TryGetTopic(topic.ID, out var known))
        {
            logger.LogError("Notification topic {Topic} isn't registered in this host, so the event on {EntityType} {RecordID} is dropped.",
                topic.ID, typeof(TEntity).Name, record.ID);
            return null;
        }

        if (record.ID == 0)
        {
            logger.LogError("Notification topic {Topic} was raised on a {EntityType} that has no ID. Raise an event after the record is saved. The event is dropped.",
                topic.ID, typeof(TEntity).Name);
            return null;
        }

        return new NotificationRaised<TEntity>
        {
            Topic = known,
            RecordID = record.ID,
            Watchers = ReadWatchers(known, record),
            Except = except?.ToHashSet() ?? [],
            Culture = CultureInfo.CurrentCulture,
            UICulture = CultureInfo.CurrentUICulture,
        };
    }

    private IReadOnlyList<NotificationWatcher> ReadWatchers<TEntity>(NotificationTopic topic, TEntity record)
        where TEntity : ShiftEntity<TEntity>, new()
    {
        if (record is not IHasNotificationWatchers withWatchers)
            return [];

        try
        {
            return withWatchers.GetNotificationWatchers()?.ToList() ?? [];
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "The watchers of {EntityType} {RecordID} failed for topic {Topic}. The event carries on without watchers.",
                typeof(TEntity).Name, record.ID, topic.ID);
            return [];
        }
    }
}
