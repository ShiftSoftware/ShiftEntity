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
public readonly record struct SavedRow(object Entity, Type EntityType, SavedRowChange Change);

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
/// <see cref="SavedAsync"/> runs right after a successful save, inside its transaction: the caller's explicit one, or
/// the one the context opens when replication trees need SQL in the save. Work that must wait for the commit, such as
/// writing to another store, goes through <see cref="ShiftDbContext.AfterCommit"/>, which runs it at once when the save
/// was already committed, and when the transaction commits otherwise. Saves made under
/// <see cref="ShiftDbContext.SuppressReplication"/> are not reported, nor are saves that do not accept their changes;
/// the roots of replication trees that such saves change are still marked as not replicated.
/// </remarks>
public interface IShiftDbContextSaveObserver
{
    /// <summary>Called after a successful save with what it wrote.</summary>
    ValueTask SavedAsync(ShiftDbContext context, SaveReport report, CancellationToken cancellationToken);
}
