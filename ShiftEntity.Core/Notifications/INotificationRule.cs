using System.Collections.Generic;

namespace ShiftSoftware.ShiftEntity.Core.Notifications;

/// <summary>
/// A rule in DI that says which topics a save of a record raises. Use it when the rule needs services, or applies to
/// several entity types through a base class or an interface. Register it with
/// <see cref="NotificationOptions.AddRule{TEntity, TRule}"/>, which also registers the class in DI. It is resolved in the
/// save's scope.
/// </summary>
/// <remarks>
/// <para>
/// It runs inside the save call, after the save has written its rows, and before the commit when a transaction is open,
/// so keep it quick. It answers a question and changes nothing: don't save from it. Code that reacts to a save by doing
/// something belongs in a save handler (<c>IAfterSaveHandler</c>, <c>IAfterCommitHandler</c>).
/// </para>
/// <para>
/// It composes with <see cref="IHasNotificationRules{TSelf}"/> and <see cref="NotificationOptions.RaiseWhenCreated{TEntity}"/>:
/// every rule that applies runs, and a topic two rules return is raised once. If it throws, the failure is logged, the
/// save isn't affected, and this rule raises nothing on that save. The saves that are never asked about are listed on
/// <see cref="IHasNotificationRules{TSelf}"/>.
/// </para>
/// <para>
/// A context from a pool or a factory has no scope of its own, so its rules are resolved from the root provider and
/// can't use scoped services.
/// </para>
/// </remarks>
/// <typeparam name="TEntity">The record's type, or a base class or interface its type has.</typeparam>
public interface INotificationRule<TEntity> where TEntity : class
{
    /// <summary>The topics this save raises on the record. An empty result is fine.</summary>
    IEnumerable<NotificationTopic> GetTopicsToRaise(NotificationChange<TEntity> change);
}
