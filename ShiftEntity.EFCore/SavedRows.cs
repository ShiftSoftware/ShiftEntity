namespace ShiftSoftware.ShiftEntity.EFCore;

/// <summary>How a save changed a row.</summary>
public enum SavedRowChange
{
    Added,
    Modified,
    Deleted,
}

/// <summary>A row that a save of a <see cref="ShiftDbContext"/> wrote.</summary>
/// <param name="Entity">The tracked instance. After a delete, the instance the context no longer tracks.</param>
/// <param name="EntityType">The entity's CLR type in the model.</param>
/// <param name="Change">How the save changed the row.</param>
public readonly record struct SavedRow(object Entity, Type EntityType, SavedRowChange Change)
{
    /// <summary>
    /// The row's values from before the save, as a detached copy of the entity: for a modified row of a type that a
    /// framework observer asked for (<see cref="ISavedRowOriginals"/>). Null for any other row, and for every added and
    /// deleted row.
    /// </summary>
    /// <remarks>
    /// The copy is EF's own picture of the row before the save (<c>OriginalValues</c>), so it holds the mapped columns
    /// only: no navigations, no owned types and no unmapped members. It is what the database held only when this context
    /// loaded the row. For a row attached as modified (<c>Update</c>), it is what the caller passed in.
    /// </remarks>
    internal object? Original { get; init; }

    /// <summary>
    /// What this save wrote, as a detached copy, when the record may have changed since: a save made from inside this
    /// save's write, such as a trigger library's after-save step, was about to write the row again. Null otherwise, and
    /// then <see cref="Entity"/> still holds what the save wrote.
    /// </summary>
    internal object? Written { get; init; }
}

/// <summary>What a successful save of a <see cref="ShiftDbContext"/> wrote.</summary>
/// <param name="Rows">The rows it wrote, in the order EF accepted them.</param>
/// <param name="ChangedRoots">
/// The roots of replication trees (see <c>HasReplicationTree</c>) that contain a row the save wrote, by root type,
/// leaving out the roots the save wrote itself, which are in <paramref name="Rows"/>. The save has already marked them
/// as not replicated, in its transaction.
/// </param>
public sealed record SaveReport(IReadOnlyList<SavedRow> Rows, IReadOnlyDictionary<Type, IReadOnlyCollection<long>> ChangedRoots);

/// <summary>
/// Is told about what each save of a <see cref="ShiftDbContext"/> wrote. A host or a framework package registers it in
/// DI; the context resolves every registration from its application service provider.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SavedAsync"/> runs right after a successful save, inside its transaction: the caller's explicit one, or
/// the one the context opens when replication trees need SQL in the save. Work that must wait for the commit, such as
/// writing to another store, goes through <see cref="ShiftDbContext.AfterCommit"/>, which runs it at once when the save
/// was already committed, and when the transaction commits otherwise. Saves made under
/// <see cref="ShiftDbContext.SuppressReplication"/> are not reported, nor are saves that do not accept their changes;
/// the roots of replication trees that such saves change are still marked as not replicated. The framework's
/// notification rules and the host's save handlers have switches of their own instead
/// (<see cref="ShiftDbContext.SuppressNotifications"/>, <see cref="ShiftDbContext.SuppressSaveHandlers"/>).
/// </para>
/// <para>
/// Host code that reacts to the rows of one entity type is simpler as a save handler
/// (<see cref="ISaveHandler{TEntity}"/>): it is resolved only when such a row was saved, it runs after every observer,
/// and it has a switch of its own (<see cref="ShiftDbContext.SaveChangesWithoutHandlersAsync"/>).
/// </para>
/// </remarks>
public interface IShiftDbContextSaveObserver
{
    /// <summary>Called after a successful save with what it wrote.</summary>
    ValueTask SavedAsync(ShiftDbContext context, SaveReport report, CancellationToken cancellationToken);
}

/// <summary>
/// A framework observer that compares a modified row with what it held before the save. The context copies those
/// values only for the entity types an observer asks for here, so a type nobody asks about costs nothing.
/// </summary>
/// <remarks>
/// Before a save writes, the context copies the old values of the rows of these types that are already modified. A row
/// of these types that becomes modified during the save (EF's own change detection, a before-save step of a trigger
/// library, the owner of an owned type) is copied at that moment, while it still holds them.
/// </remarks>
internal interface ISavedRowOriginals
{
    /// <summary>Whether this observer needs the old values of modified rows of this type. Asked once per type and context.</summary>
    bool NeedsOriginalOf(Type entityType);
}

/// <summary>When a framework observer hears about a save, among the context's observers.</summary>
internal enum SaveObserverOrder
{
    /// <summary>Before every other observer, while each record still holds exactly what the save wrote.</summary>
    First = -1,

    /// <summary>In the order the observers were registered. Any observer that doesn't say otherwise.</summary>
    Registered = 0,

    /// <summary>After every other observer: host code that may change the saved records and save again.</summary>
    Last = 1,
}

/// <summary>A framework observer with a fixed place among the context's observers.</summary>
internal interface IOrderedSaveObserver
{
    SaveObserverOrder Order { get; }
}

/// <summary>
/// A framework observer with a switch of its own: the notification rules
/// (<see cref="ShiftDbContext.SuppressNotifications"/>) and the host's save handlers
/// (<see cref="ShiftDbContext.SuppressSaveHandlers"/>). Its own switch silences it, and
/// <see cref="ShiftDbContext.SuppressReplication"/>, which keeps rows away from other stores, doesn't.
/// </summary>
internal interface ISwitchedSaveObserver
{
    /// <summary>Whether this observer's switch is on for a save that the context starts now.</summary>
    bool IsSuppressed(ShiftDbContext context);
}
