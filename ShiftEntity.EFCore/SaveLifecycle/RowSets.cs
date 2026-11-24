using Microsoft.EntityFrameworkCore.ChangeTracking;
using System.Collections;

namespace ShiftSoftware.ShiftEntity.EFCore;

/// <summary>The rows of one type that a handler is called with.</summary>
/// <typeparam name="TEntity">The type the handler is written for.</typeparam>
public abstract class RowSet<TEntity> : IReadOnlyList<ChangedRow<TEntity>> where TEntity : class
{
    // A set a test built holds its rows. A set the context made is a view over the save's list: of the rows it is
    // about to write, or of the rows it wrote.
    private readonly ChangedRow<TEntity>[]? built;
    private readonly SavedRowView? saved;
    private readonly PendingRowView? pending;
    private readonly SaveUnit? unit;
    private Dictionary<object, object?>? ownItems;

    private protected RowSet(ShiftDbContext db, ChangedRow<TEntity>[] rows)
    {
        Db = db;
        built = rows;
    }

    private protected RowSet(ShiftDbContext db, SaveUnit unit, SavedRowView view)
    {
        Db = db;
        this.unit = unit;
        saved = view;
    }

    private protected RowSet(ShiftDbContext db, SaveUnit unit, PendingRowView view)
    {
        Db = db;
        this.unit = unit;
        pending = view;
    }

    /// <summary>
    /// The context that is saving. Use it to read, to save again where the stage allows it, and for
    /// <see cref="ShiftDbContext.AfterCommit(Action)"/>. A context the handler had injected is the same object only
    /// when both come from one scope.
    /// </summary>
    public ShiftDbContext Db { get; }

    /// <summary>
    /// A bag shared by every handler and stage of what commits together, for passing something from one stage, round
    /// or save to a later one. Keep such state here, not in a handler's fields: a declared handler is a new instance
    /// for each stage call, and a registered one lives as long as its scope.
    /// </summary>
    /// <remarks>
    /// The bag is rolled back with the saves. A save that fails, and a rollback to a savepoint, put it back as it was
    /// when that save began, or when the savepoint was set: the keys added since are removed, and the values replaced
    /// or removed since come back. The copy that is put back is shallow, so an object that a handler changed in place
    /// is not put back. Store values, or replace an object with a new one, rather than changing it.
    /// </remarks>
    public IDictionary<object, object?> Items => unit is not null ? unit.Items : ownItems ??= [];

    /// <summary>
    /// The services of the save: those of the scope the context came from, or the ones it was given with
    /// <see cref="ShiftDbContext.AttachServices"/>. For a handler whose constructor takes nothing, which is created
    /// without services and asks here for what it needs when it needs it.
    /// </summary>
    /// <remarks>
    /// A context that has no services, such as one a tool builds with <c>new</c>, gives a provider with nothing
    /// registered: <c>GetService</c> returns null there, and <c>GetRequiredService</c> throws. It is never null. A
    /// context from a pool or a factory gives the root provider until it is given the services of a scope, so a scoped
    /// service asked of it there would live as long as the host. A set that <see cref="HandlerRows"/> built for a test
    /// gives the services of the context it was built with, by the same rule, as they are when it is asked. A handler
    /// that takes its services in its constructor has no use for this.
    /// </remarks>
    public IServiceProvider Services => Db.HandlerServices;

    /// <summary>
    /// Takes a lock on a name and holds it until the save's transaction ends, so that one transaction at a time does
    /// what the name stands for, such as giving out the next number of a series. Waits while another transaction holds
    /// it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A before-save handler needs nothing else: the call asks for the transaction first, as
    /// <see cref="PendingRows{TEntity}.RequireTransactionAsync"/> does. An after-save and a before-commit handler are in
    /// it already. After the commit the save's transaction is gone, and the call throws, unless the handler has begun a
    /// transaction of its own.
    /// </para>
    /// <para>
    /// Read what the lock protects after taking it, and with a projection or <c>AsNoTracking</c>. The rows themselves were
    /// loaded before. A handler that takes several locks takes them in one fixed order, for example sorted by name, so
    /// that two saves do not each wait for a lock the other one holds. On a set that a test built it does nothing.
    /// </para>
    /// <para>
    /// Take it each time, also when an earlier call of the handler in the same transaction took it. What is kept in
    /// <see cref="Items"/> does not show that the lock is still held: a rollback to a savepoint may have released it.
    /// </para>
    /// <para>
    /// <see cref="ShiftDatabaseFacade.TakeLockAsync"/> takes the same lock outside a handler, and says how long it waits
    /// and what it does on each database.
    /// </para>
    /// </remarks>
    /// <param name="name">What is locked: a name of the app's own, up to 255 characters, such as <c>InvoiceNo:12</c>.</param>
    /// <param name="cancellationToken">Ends the wait.</param>
    public ValueTask TakeLockAsync(string name, CancellationToken cancellationToken = default)
    {
        DatabaseLock.CheckName(name);

        return unit is null ? ValueTask.CompletedTask : TakeLockInASaveAsync(name, cancellationToken);
    }

    private protected virtual async ValueTask TakeLockInASaveAsync(string name, CancellationToken cancellationToken) =>
        await Db.Database.TakeLockAsync(name, cancellationToken).ConfigureAwait(false);

    public int Count => built?.Length ?? saved?.Count ?? pending!.Count;

    public ChangedRow<TEntity> this[int index] =>
        built is not null ? built[index] : saved is not null ? saved.Row<TEntity>(index) : pending!.Row<TEntity>(index);

    public IEnumerator<ChangedRow<TEntity>> GetEnumerator()
    {
        for (var i = 0; i < Count; i++)
            yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// The rows a before-save handler is called with: the rows of the type that the save is about to write and that the
/// handler has not been offered yet in this save, in the order the context tracks them.
/// </summary>
/// <remarks>
/// A row is offered once per save for each kind of change. A row that another handler adds or changes is offered in
/// the next round. A row that is already offered as modified is not offered again when a later handler changes it.
/// </remarks>
/// <typeparam name="TEntity">The type the handler is written for.</typeparam>
public sealed class PendingRows<TEntity> : RowSet<TEntity> where TEntity : class
{
    // Whether the context made the set for a save in progress, and not a test.
    private readonly bool inASave;

    // The stages in which the save's switches keep its rows from the handler class the set was made for.
    private readonly SaveStages switchedOff;

    internal PendingRows(ShiftDbContext db, ChangedRow<TEntity>[] rows, bool isRepositorySave = false) : base(db, rows)
    {
        IsRepositorySave = isRepositorySave;
    }

    internal PendingRows(ShiftDbContext db, SaveUnit unit, PendingRowView view, bool isRepositorySave, SaveStages switchedOff) : base(db, unit, view)
    {
        IsRepositorySave = isRepositorySave;
        inASave = true;
        this.switchedOff = switchedOff;
    }

    /// <summary>
    /// Whether a switch keeps this save's rows from this handler in one of the named stages: a
    /// <c>SuppressSaveHandler</c> that names the handler class, a <c>SuppressSaveHandlers</c> that names the stage,
    /// <c>SuppressAllSaveHandlers</c>, and for the after-commit stage <c>SuppressAfterCommit</c>. A guard is switched
    /// off only by name.
    /// </summary>
    /// <remarks>
    /// For a handler that hands something on to a later stage of its own through <see cref="RowSet{TEntity}.Items"/>:
    /// when that stage will not get this save's rows, what it hands on would stay in the bag for a later save of the
    /// same transaction to find. So it hands on nothing. On a set that a test built it is false.
    /// </remarks>
    /// <param name="stages">The stages, combined with <c>|</c>.</param>
    public bool IsSwitchedOff(SaveStages stages) => (switchedOff & stages) != 0;

    /// <summary>
    /// Whether the save was started by a repository's <c>SaveChangesAsync</c>. False for a save made from inside it,
    /// and for any save made through the context itself.
    /// </summary>
    public bool IsRepositorySave { get; }

    /// <summary>EF's entry of a row, for property-level questions: <c>IsModified</c>, <c>OriginalValue</c>.</summary>
    public EntityEntry<TEntity> Entry(ChangedRow<TEntity> row) => Db.Entry(row.Entity);

    /// <summary>
    /// From here on, what this handler reads and writes, the write and everything after it are one transaction. Opens
    /// the context's own transaction when none is open. <see cref="RowSet{TEntity}.TakeLockAsync"/> asks for it by itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The context's own transaction is committed by the save, after its before-commit handlers, and rolled back as a
    /// whole when the save fails.
    /// </para>
    /// <para>
    /// Inside a transaction the caller began, the save's savepoint is set here. When the save fails, the transaction
    /// is rolled back to it, which undoes what this handler wrote with SQL of its own from here on (a counter it moved
    /// on, for example) as well as the save's write, and the caller's transaction can go on. Where no savepoint can be
    /// set (<c>MultipleActiveResultSets=True</c> on SQL Server, savepoints switched off, or an ambient or enlisted
    /// transaction), what this handler wrote cannot be undone alone. So a failure of the save from here on, before its
    /// write as after it, leaves that transaction able only to roll back: the commit of a transaction begun through
    /// <see cref="ShiftDbContext.Database"/> is refused, and an ambient or enlisted one is rolled back.
    /// </para>
    /// <para>
    /// The call is refused where the context cannot open a transaction of its own: under an execution strategy that
    /// retries, and with <c>AutoTransactionBehavior.Never</c>. On a set that a test built it does nothing.
    /// </para>
    /// </remarks>
    public ValueTask RequireTransactionAsync(CancellationToken cancellationToken = default) =>
        inASave ? Db.RequireTransactionAsync(nameof(RequireTransactionAsync), cancellationToken) : ValueTask.CompletedTask;

    private protected override async ValueTask TakeLockInASaveAsync(string name, CancellationToken cancellationToken)
    {
        await Db.RequireTransactionAsync(nameof(TakeLockAsync), cancellationToken).ConfigureAwait(false);
        await base.TakeLockInASaveAsync(name, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// The rows a stage after the write is called with. After save: the rows of the type that the save wrote, in the order
/// EF accepted them. Before commit and after commit: one row per record for everything that commits together, however
/// many saves wrote the record.
/// </summary>
/// <remarks>
/// A record saved more than once is matched by its entity type and key. Added and then modified is an added row.
/// Added and then deleted is no row. Modified and then deleted is a deleted row. Deleted and then added again with the
/// same key is a modified row. The record is the latest instance, and
/// <see cref="ChangedRow{TEntity}.Original"/> is what it held before the first of those saves.
/// </remarks>
/// <typeparam name="TEntity">The type the handler is written for.</typeparam>
public sealed class SavedRows<TEntity> : RowSet<TEntity> where TEntity : class
{
    internal SavedRows(ShiftDbContext db, ChangedRow<TEntity>[] rows) : base(db, rows) { }

    internal SavedRows(ShiftDbContext db, SaveUnit unit, SavedRowView view) : base(db, unit, view) { }
}

/// <summary>
/// The rows of one handler call, picked from a save's or a unit's list. A typed row is made only when the handler
/// reads it.
/// </summary>
internal sealed class SavedRowView(IReadOnlyList<SavedRow> rows, int[]? picked, int count, bool hasOriginals, Type handlerType)
{
    public int Count => count;

    public ChangedRow<TEntity> Row<TEntity>(int index) where TEntity : class
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)count, nameof(index));

        var row = rows[picked is null ? index : picked[index]];

        return new ChangedRow<TEntity>(
            (TEntity)row.Entity,
            (RowChange)row.Change,
            hasOriginals ? (TEntity?)row.Original : null,
            row.IsSoftDelete,
            hasOriginals ? null : handlerType);
    }
}

/// <summary>A row that a save is about to write, as one round of the before-save stage found it.</summary>
/// <param name="Entry">EF's entry of the row.</param>
/// <param name="Change">How the save will change the row, as the round's look at the tracker found it.</param>
/// <param name="EntityType">The entity's CLR type in the model.</param>
internal readonly record struct PendingRow(EntityEntry Entry, RowChange Change, Type EntityType);

/// <summary>The rows of one before-save handler call, picked from the rows of a round.</summary>
internal sealed class PendingRowView(List<PendingRow> rows, int[] picked, int count)
{
    public int Count => count;

    public ChangedRow<TEntity> Row<TEntity>(int index) where TEntity : class
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)count, nameof(index));

        var row = rows[picked[index]];

        return new ChangedRow<TEntity>((TEntity)row.Entry.Entity, row.Change, row.Entry);
    }
}
