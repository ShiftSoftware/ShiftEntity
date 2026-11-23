using System;

namespace ShiftSoftware.ShiftEntity.Core.Notifications;

/// <summary>
/// What one save did to a record, for the rules that decide which topics it raises. The framework builds it after the
/// save has written the row, and raises what the rules return once the save has committed.
/// </summary>
/// <typeparam name="TEntity">The record's type, or the base class or interface a rule is written for.</typeparam>
public sealed class NotificationChange<TEntity> where TEntity : class
{
    /// <summary>Whether this save created the record. Otherwise it changed one that already existed.</summary>
    public required bool IsCreated { get; init; }

    /// <summary>The record as this save wrote it.</summary>
    public required TEntity Entity { get; init; }

    /// <summary>The record's values from before this save, as a detached copy. Null when this save created it.</summary>
    /// <remarks>
    /// <para>
    /// The copy is the context's own picture of the row before the save, so it holds the record's mapped columns only.
    /// Navigations, owned types and members that aren't mapped are not copied: a rule that reads one of them here
    /// reads a default value, and would see a change on every save.
    /// </para>
    /// <para>
    /// It is what the database held only when the saving context loaded the record. For a record attached as modified
    /// without being loaded (<c>Update</c>), it is what the caller passed in, so nothing "became" anything on that save.
    /// </para>
    /// </remarks>
    public TEntity? Original { get; init; }

    /// <summary>The save's services, for a rule that needs to look something up. Most rules don't.</summary>
    public required IServiceProvider Services { get; init; }

    /// <summary>
    /// Whether the condition holds now and didn't before this save, such as "the status is Resolved". On a record this
    /// save created, it's whether the condition holds.
    /// </summary>
    /// <remarks>
    /// Write the condition on the record's own columns. See <see cref="Original"/> for what the values from before the
    /// save don't hold.
    /// </remarks>
    public bool Became(Func<TEntity, bool> condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        return condition(Entity) && (Original is null || !condition(Original));
    }
}
