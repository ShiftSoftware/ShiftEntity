namespace ShiftSoftware.ShiftEntity.EFCore;

/// <summary>
/// App code that runs inside a save, before anything is written. For checking and preparing the records, and for
/// adding related ones. Declare it with the context (<c>ConfigureSaveHandlers</c>), with a module
/// (<see cref="IModelBuildingContributor.ConfigureSaveHandlers"/>), or with the host
/// (<c>services.AddSaveHandler&lt;THandler&gt;()</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>When.</b> For every save of a <see cref="ShiftDbContext"/> that is about to write a row of
/// <typeparamref name="TEntity"/>: through a repository, a plain <c>SaveChanges</c> or a save made by another handler.
/// It is called with all the rows of its type that it has not been offered yet in this save, after the audit columns
/// of those rows are stamped.
/// </para>
/// <para>
/// <b>Rounds.</b> A handler may change its rows, and add, change or remove other tracked rows. Those are offered to
/// the handlers of their own type in the next round, so a handler may be called more than once in one save, each time
/// with other rows. A row is offered once per save for each kind of change. A save whose handlers are still producing
/// rows after 16 rounds throws.
/// </para>
/// <para>
/// <b>Not allowed.</b> Calling <c>SaveChanges</c> on the saving context: the save in progress is about to write the
/// tracked rows. The context throws instead.
/// </para>
/// <para>
/// <b>Transaction.</b> Inside the caller's when one is open. A handler that needs its reads and the write to be one
/// transaction asks with <see cref="PendingRows{TEntity}.RequireTransactionAsync"/>. One that reads under a lock, such as
/// the last number of a series, takes it with <see cref="RowSet{TEntity}.TakeLockAsync"/>, which asks by itself. Inside the
/// caller's transaction either call also sets the save's savepoint, so that a failure of the save undoes SQL the handler
/// runs itself from then on, together with the save's write. Where no savepoint can be set, such a failure leaves the
/// caller's transaction able only to roll back. SQL a handler runs before it asks, or without asking, is not covered: a
/// failure inside the caller's transaction leaves it in that transaction.
/// </para>
/// <para>
/// <b>Failures.</b> An exception leaves the save call unchanged, and nothing was written. When the caller saves again,
/// the handler runs again for the same rows, so a handler that adds a row checks that it has not added it already.
/// </para>
/// <para>
/// <b>State.</b> A handler that the host registers (<c>AddSaveHandler</c>, scoped by default) is one instance per
/// scope, across stages and saves. One that is only declared with the context or a module is a new instance for each
/// stage call. So a handler keeps what it passes on, to a later round or a later stage, in
/// <see cref="RowSet{TEntity}.Items"/>, not in fields.
/// </para>
/// </remarks>
/// <typeparam name="TEntity">The entity type, or a base class or an interface, whose rows the handler gets.</typeparam>
public interface IBeforeSaveHandler<TEntity> where TEntity : class
{
    /// <summary>Called for a save, with the rows of the type that it is about to write.</summary>
    ValueTask BeforeSaveAsync(PendingRows<TEntity> rows, CancellationToken cancellationToken);
}

/// <summary>
/// App code that runs inside a save, once EF has written and accepted every row of the save. For a write that must
/// succeed or fail together with the save, such as a number taken under a lock or a follow-up record. Declare it with
/// the context (<c>ConfigureSaveHandlers</c>), with a module
/// (<see cref="IModelBuildingContributor.ConfigureSaveHandlers"/>), or with the host
/// (<c>services.AddSaveHandler&lt;THandler&gt;()</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>When.</b> For every save of a <see cref="ShiftDbContext"/> that writes a row of <typeparamref name="TEntity"/>:
/// through a repository, a plain <c>SaveChanges</c> or a save made by another handler. It is called once per save, with
/// all the rows of its type that the save wrote, after the framework's own steps.
/// </para>
/// <para>
/// <b>Transaction.</b> Always inside one: the caller's, or one the context opens for the save because this handler
/// applies to its rows. A call to another system does not belong here, because the transaction can still roll back:
/// that is <see cref="IAfterCommitHandler{TEntity}"/>.
/// </para>
/// <para>
/// <b>Saving again.</b> The handler may change records and save through <see cref="RowSet{TEntity}.Db"/>. That save
/// runs in the same transaction, and runs the handlers of the rows it writes. This handler is not called again for a
/// record it is being called for.
/// </para>
/// <para>
/// <b>Failures.</b> An exception leaves the save call and the write is undone: the context's own transaction is rolled
/// back, and a caller's transaction is rolled back to where the save began. EF had accepted the rows, so the context
/// still shows them as saved, and it refuses every later save until those rows are gone from its tracker. To go on,
/// call <c>ChangeTracker.Clear()</c>, detach every row of that save, or use a new context. Adding or changing the same
/// instances again does not end the refusal: a save that is tried again starts from a new context, or from a cleared
/// change tracker, on each attempt.
/// </para>
/// <para>
/// <b>State.</b> A handler that the host registers is one instance per scope, across stages and saves; one that is
/// only declared is a new instance for each stage call. Keep what passes from one stage to another in
/// <see cref="RowSet{TEntity}.Items"/>, not in fields.
/// </para>
/// </remarks>
/// <typeparam name="TEntity">The entity type, or a base class or an interface, whose rows the handler gets.</typeparam>
public interface IAfterSaveHandler<TEntity> where TEntity : class
{
    /// <summary>Called once for a save, with the rows of the type that it wrote.</summary>
    ValueTask AfterSaveAsync(SavedRows<TEntity> rows, CancellationToken cancellationToken);
}

/// <summary>
/// App code that runs once just before a transaction commits, over everything the transaction saved. For last checks
/// and for totals over all the rows. Declare it with the context (<c>ConfigureSaveHandlers</c>), with a module
/// (<see cref="IModelBuildingContributor.ConfigureSaveHandlers"/>), or with the host
/// (<c>services.AddSaveHandler&lt;THandler&gt;()</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>When.</b> Once per transaction: inside <c>Commit</c> for a transaction the app began through
/// <see cref="ShiftDbContext.Database"/>, and inside the save call for a save the context gave a transaction of its
/// own. It gets one row per record, however many saves of the transaction wrote that record.
/// </para>
/// <para>
/// <b>Saving again.</b> A save made here joins the transaction. When it writes rows that have before-commit handlers,
/// those run in another round.
/// </para>
/// <para>
/// <b>Failures.</b> An exception leaves the call that commits, and the commit does not happen. The context's own
/// transaction is rolled back. A transaction of the app's stays open and can only be rolled back.
/// </para>
/// </remarks>
/// <typeparam name="TEntity">The entity type, or a base class or an interface, whose rows the handler gets.</typeparam>
public interface IBeforeCommitHandler<TEntity> where TEntity : class
{
    /// <summary>Called once for a transaction that is about to commit, with the rows of the type that it saved.</summary>
    ValueTask BeforeCommitAsync(SavedRows<TEntity> rows, CancellationToken cancellationToken);
}

/// <summary>
/// App code that runs once the rows are committed. For telling other systems: a message, an email, a call to an API.
/// Declare it with the context (<c>ConfigureSaveHandlers</c>), with a module
/// (<see cref="IModelBuildingContributor.ConfigureSaveHandlers"/>), or with the host
/// (<c>services.AddSaveHandler&lt;THandler&gt;()</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>When.</b> Once per commit, when the transaction is gone: inside <c>Commit</c> for a transaction the app began
/// through <see cref="ShiftDbContext.Database"/>, and inside the save call otherwise. A save that needed no transaction
/// is committed by its write. It gets one row per record, as <see cref="IBeforeCommitHandler{TEntity}"/> does. A
/// transaction that rolls back calls nobody.
/// </para>
/// <para>
/// <b>Saving again.</b> The handler may use the context and save. That save is a new one, with its own commit, and it
/// ends before the next handler starts. This handler is not called again for a record it is being called for.
/// </para>
/// <para>
/// <b>Failures.</b> Nothing is undone, and every other handler still runs. The call that committed then throws one
/// <see cref="AfterCommitException"/> that holds every failure.
/// </para>
/// <para>
/// <b>Cancellation.</b> The token is not the caller's: a request that is aborted after the commit must not lose the
/// message. It is cancelled only when the host is stopping.
/// </para>
/// </remarks>
/// <typeparam name="TEntity">The entity type, or a base class or an interface, whose rows the handler gets.</typeparam>
public interface IAfterCommitHandler<TEntity> where TEntity : class
{
    /// <summary>Called once for a commit, with the rows of the type that it committed.</summary>
    ValueTask AfterCommitAsync(SavedRows<TEntity> rows, CancellationToken cancellationToken);
}
