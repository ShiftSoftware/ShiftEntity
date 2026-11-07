using ShiftSoftware.ShiftEntity.Model.Notifications;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Core.Notifications;

/// <summary>
/// How the send path reads the subscribers list. It is this one interface, so the list can move later. It is called
/// once per event, in the event's own scope.
/// </summary>
public interface INotificationSubscribersList
{
    /// <summary>
    /// All the entries of the list. All of them, and not only one topic's, so that an entry naming a topic the code no
    /// longer declares is logged at send time.
    /// </summary>
    Task<IReadOnlyList<NotificationSubscribersEntryDTO>> GetEntriesAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// A subscribers list held in memory. <c>AddNotifications</c> registers an empty one, so a host that only uses watchers
/// needs no file. <c>AddNotificationSubscribersJson</c> takes its place, and so does a host's own list.
/// </summary>
public sealed class InMemoryNotificationSubscribersList : INotificationSubscribersList
{
    private readonly object gate = new();
    private List<NotificationSubscribersEntryDTO> entries;

    public InMemoryNotificationSubscribersList(IEnumerable<NotificationSubscribersEntryDTO>? entries = null)
    {
        this.entries = entries?.ToList() ?? [];
    }

    /// <summary>Replaces the whole list.</summary>
    public void SetEntries(IEnumerable<NotificationSubscribersEntryDTO> entries)
    {
        lock (gate)
            this.entries = entries.ToList();
    }

    public Task<IReadOnlyList<NotificationSubscribersEntryDTO>> GetEntriesAsync(CancellationToken cancellationToken = default)
    {
        lock (gate)
            return Task.FromResult<IReadOnlyList<NotificationSubscribersEntryDTO>>(entries);
    }
}
