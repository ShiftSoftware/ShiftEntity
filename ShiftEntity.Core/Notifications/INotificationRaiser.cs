using System.Collections.Generic;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Core.Notifications;

/// <summary>
/// Announces that something happened to a record. The host keeps the decision about when: it calls this from an
/// after-save trigger or a repository hook. The framework works out who hears, checks who may hear, and sends the
/// email in the background. <c>AddNotifications</c> registers this, so it is resolved in the same scope as the save.
/// </summary>
public interface INotificationRaiser
{
    /// <summary>
    /// Queues the event. If a transaction is open on the host's context, the event waits for its commit, and a rollback
    /// drops it. A failure here is logged and never reaches the save.
    /// </summary>
    /// <remarks>
    /// Call this after the record is saved. A record that has no ID yet is refused and logged. The watchers run now, and
    /// the culture of this call is the language of the email. The record itself is read again in the background, so the
    /// email shows what was committed.
    /// </remarks>
    /// <param name="topic">One of the host's registered topics.</param>
    /// <param name="record">The saved record.</param>
    /// <param name="except">Users to leave out, such as the person who made the change.</param>
    Task RaiseAsync<TEntity>(NotificationTopic topic, TEntity record, IEnumerable<long>? except = null)
        where TEntity : ShiftEntity<TEntity>, new();
}
