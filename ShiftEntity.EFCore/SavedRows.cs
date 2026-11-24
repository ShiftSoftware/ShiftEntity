namespace ShiftSoftware.ShiftEntity.EFCore;

/// <summary>How a save changed a row.</summary>
internal enum SavedRowChange
{
    Added,
    Modified,
    Deleted,
}

/// <summary>A row that a save of a <see cref="ShiftDbContext"/> wrote.</summary>
/// <param name="Entity">The tracked instance. After a delete, the instance the context no longer tracks.</param>
/// <param name="EntityType">The entity's CLR type in the model.</param>
/// <param name="Change">How the save changed the row.</param>
internal readonly record struct SavedRow(object Entity, Type EntityType, SavedRowChange Change)
{
    /// <summary>
    /// The row's values from before the save, as a detached copy of the entity: for a modified row of a type that a
    /// framework observer asked for (<see cref="ISavedRowOriginals"/>), and for a modified or deleted row of a type with
    /// a handler marked <see cref="NeedsOriginalAttribute"/>. Null for any other row, and for every added row.
    /// </summary>
    /// <remarks>
    /// The copy is EF's own picture of the row before the save (<c>OriginalValues</c>), so it holds the mapped columns
    /// only: no navigations, no owned types and no unmapped members. It is what the database held only when this context
    /// loaded the row. For a row attached as modified (<c>Update</c>), it is what the caller passed in.
    /// </remarks>
    public object? Original { get; init; }

    /// <summary>What else the context noted about the row, for the handlers.</summary>
    public SavedRowFlags Flags { get; init; }

    public bool IsSoftDelete => (Flags & SavedRowFlags.SoftDeleted) != 0;

    public bool IsOwnedPart => (Flags & SavedRowFlags.OwnedPart) != 0;
}

[Flags]
internal enum SavedRowFlags : byte
{
    None = 0,

    /// <summary>A modified row whose <c>IsDeleted</c> the save turned from false to true.</summary>
    SoftDeleted = 1,

    /// <summary>A modified row whose <c>IsDeleted</c> the save turned from true to false.</summary>
    Restored = 2,

    /// <summary>An entry of an owned type. Its owner is the row, so no handler gets it.</summary>
    OwnedPart = 4,
}

/// <summary>What a successful save of a <see cref="ShiftDbContext"/> wrote, as its observers are told.</summary>
/// <param name="Rows">
/// The rows it wrote of the types an observer or a handler asked for, in the order EF accepted them.
/// </param>
/// <param name="ChangedRoots">
/// The roots of replication trees (see <c>HasReplicationTree</c>) that contain a row the save wrote, by root type,
/// leaving out the roots the save wrote itself, which are in <paramref name="Rows"/>. The save has already marked them
/// as not replicated, in its transaction.
/// </param>
internal sealed record SaveReport(IReadOnlyList<SavedRow> Rows, IReadOnlyDictionary<Type, IReadOnlyCollection<long>> ChangedRoots);

/// <summary>
/// A part of the framework that is told what each save of a <see cref="ShiftDbContext"/> wrote: the notification
/// rules, and replication. A framework package registers it in DI, and the context resolves every registration from
/// its services. App code takes part in a save through handlers instead
/// (<see cref="IAfterSaveHandler{TEntity}"/>, <see cref="IAfterCommitHandler{TEntity}"/>).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SavedAsync"/> runs right after a successful save, before the after-save handlers. In a transaction, the
/// caller's or the one the context opened for the save, it runs inside it, and a failure here fails the save. Work
/// that must wait for the commit, such as writing to another store, goes through
/// <see cref="ShiftDbContext.AfterCommit(Action)"/>.
/// </para>
/// <para>
/// In a save without a transaction the write was the commit, so a failure here undoes nothing: the other observers,
/// the queued work and the after-commit handlers still run, and the save call throws one
/// <see cref="AfterCommitException"/>.
/// </para>
/// <para>
/// A save is not reported under <see cref="ShiftDbContext.SuppressAfterCommit"/>, nor under the observer's own
/// switch: <see cref="ShiftDbContext.SuppressNotifications"/> for the notification rules, and
/// <see cref="ShiftDbContext.SuppressReplication"/> for any other observer. The roots of replication trees that such
/// a save changes are still marked as not replicated.
/// </para>
/// </remarks>
internal interface IShiftDbContextSaveObserver
{
    /// <summary>
    /// Whether the observer wants the rows of this type that a save of the context writes. The context collects rows
    /// only for the types that an observer or a handler asks for. Asked once per type and context.
    /// </summary>
    bool Wants(ShiftDbContext context, Type entityType) => true;

    /// <summary>Called after a successful save with what it wrote.</summary>
    ValueTask SavedAsync(ShiftDbContext context, SaveReport report, CancellationToken cancellationToken);
}

/// <summary>
/// A framework observer that compares a modified row with what it held before the save. The context copies those
/// values only for the entity types an observer asks for here, so a type nobody asks about costs nothing.
/// </summary>
/// <remarks>
/// Before a save writes, the context copies the old values of the rows of these types that are already modified. A row
/// of these types that becomes modified during the save (EF's own change detection, an interceptor of the app's, the
/// owner of an owned type) is copied at that moment, while it still holds them.
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

    /// <summary>After every other observer.</summary>
    Last = 1,
}

/// <summary>A framework observer with a fixed place among the context's observers.</summary>
internal interface IOrderedSaveObserver
{
    SaveObserverOrder Order { get; }
}

/// <summary>
/// A framework observer with a switch of its own: the notification rules
/// (<see cref="ShiftDbContext.SuppressNotifications"/>). Its own switch silences it, and
/// <see cref="ShiftDbContext.SuppressReplication"/>, which keeps rows away from other stores, doesn't.
/// </summary>
internal interface ISwitchedSaveObserver
{
    /// <summary>Whether this observer's switch is on for a save that the context starts now.</summary>
    bool IsSuppressed(ShiftDbContext context);
}

/// <summary>
/// A framework observer that queues work for after the commit, and only for some rows. A save that cannot serve such
/// work asks before it writes: a save inside a transaction the context cannot follow, where the work could never run
/// at the right time, and a save that does not accept its changes, whose rows no observer is told about. Such a save
/// is refused while nothing has been written.
/// </summary>
internal interface IWaitsForCommitSaveObserver
{
    /// <summary>What waits, as the refusal names it.</summary>
    string Work { get; }

    /// <summary>The switch that turns the work off for a save, as the refusal names it.</summary>
    string Switch { get; }

    /// <summary>
    /// Whether a save of the context has work of this observer that waits for the commit when it writes a row of the
    /// type or, with <paramref name="asChangedRoot"/>, when it changes a replication tree whose root is of the type.
    /// </summary>
    bool WaitsFor(ShiftDbContext context, Type entityType, bool asChangedRoot);
}
