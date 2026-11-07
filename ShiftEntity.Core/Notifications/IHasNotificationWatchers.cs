using System.Collections.Generic;

namespace ShiftSoftware.ShiftEntity.Core.Notifications;

/// <summary>
/// Lets an entity name the people attached to it, each with their topics, without DI. Implement it on the entity class
/// when people care about a record because of who they are on it, such as its customer or the person who created it.
/// Nobody has to add them to a subscribers list.
/// </summary>
/// <remarks>
/// It runs when an event is raised, and the result is not stored, so the list is never out of date and the entity's
/// table gains nothing. Read only the record's own loaded values here. If it throws, the failure is logged, the save
/// isn't affected, and the event carries on without watchers.
/// </remarks>
public interface IHasNotificationWatchers
{
    /// <summary>The people attached to this record. An empty result is fine.</summary>
    IEnumerable<NotificationWatcher> GetNotificationWatchers();
}
