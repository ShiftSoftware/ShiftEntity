using System.Collections.Generic;

namespace ShiftSoftware.ShiftEntity.Core.Notifications;

/// <summary>
/// Lets an entity say which topics a save of it raises, without DI and without a save handler. Implement it on the entity class
/// for moments only the host knows, such as "the status became Resolved".
/// </summary>
/// <remarks>
/// <para>
/// The other two ways compose with this one: <see cref="NotificationOptions.RaiseWhenCreated{TEntity}"/> for "raise when
/// created" in one line, and <see cref="INotificationRule{TEntity}"/> for a rule that needs services.
/// </para>
/// <para>
/// The framework asks after every save of the host's context that writes the record, through a repository or a plain
/// <c>SaveChanges</c>, and raises what it returns once the save has committed. Read only the record's own values here. If
/// it throws, the failure is logged, the save isn't affected, and this save raises nothing for that record.
/// </para>
/// <para>
/// Some saves are never asked about: a save under <c>SuppressNotifications</c>, a save that doesn't accept its changes
/// (<c>acceptAllChangesOnSuccess: false</c>), and a write that goes around the change tracker (<c>ExecuteUpdate</c>,
/// <c>ExecuteDelete</c>, raw SQL). See <see cref="NotificationChange{TEntity}.Original"/> for what a rule about a change
/// can and can't compare.
/// </para>
/// </remarks>
/// <typeparam name="TSelf">The entity type, by convention the implementing class itself.</typeparam>
public interface IHasNotificationRules<TSelf> where TSelf : class
{
    /// <summary>The topics this save raises on the record. An empty result is fine.</summary>
    IEnumerable<NotificationTopic> GetTopicsToRaise(NotificationChange<TSelf> change);
}
