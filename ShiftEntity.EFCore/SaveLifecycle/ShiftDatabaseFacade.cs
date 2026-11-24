using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using System.Data;

namespace ShiftSoftware.ShiftEntity.EFCore;

/// <summary>
/// The <c>Database</c> of a <see cref="ShiftDbContext"/>: EF's <see cref="DatabaseFacade"/>, with one method more
/// (<see cref="TakeLockAsync"/>). It follows the transactions that are begun through it, so that the context knows when they
/// commit and when they roll back.
/// </summary>
/// <remarks>
/// <para>
/// A transaction begun here is handed out in a wrapper. Its <c>Commit</c> runs the before-commit handlers, EF's
/// commit, and then what waited for the commit: the queued work and the after-commit handlers. A rollback, and a
/// transaction disposed without a commit, drop what waited.
/// </para>
/// <para>
/// A transaction the context cannot follow is one begun around this class: <c>UseTransaction</c>, a
/// <c>TransactionScope</c>, or <c>BeginTransaction(IsolationLevel)</c> called through a variable typed
/// <see cref="DbContext"/>, where C# binds to EF's extension method. A save inside one is refused when it has work
/// that waits for the commit.
/// </para>
/// </remarks>
public sealed class ShiftDatabaseFacade : DatabaseFacade
{
    private readonly ShiftDbContext context;

    // The transaction that was begun here and has not ended.
    private ShiftDbContextTransaction? followed;

    // The transaction the app began here that ended last, until the app begins or uses another one. A rollback through
    // this facade after it committed does nothing, as the transaction's own rollback does. A transaction the context
    // opens for one save is not the app's: it never sets or clears this.
    private ShiftDbContextTransaction? lastEnded;

    internal ShiftDatabaseFacade(ShiftDbContext context) : base(context)
    {
        this.context = context;
    }

    public override IDbContextTransaction BeginTransaction()
    {
        context.RefuseToBeginInsideASave();
        return Follow(base.BeginTransaction(), unit: null);
    }

    public override async Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        context.RefuseToBeginInsideASave();
        return Follow(await base.BeginTransactionAsync(cancellationToken), unit: null);
    }

    /// <summary>
    /// Begins a transaction with an isolation level, and follows it. An instance method, because C# prefers it to EF's
    /// extension method of the same name, which begins the transaction around this class.
    /// </summary>
    public IDbContextTransaction BeginTransaction(IsolationLevel isolationLevel)
    {
        context.RefuseToBeginInsideASave();
        return Follow(RelationalDatabaseFacadeExtensions.BeginTransaction(this, isolationLevel), unit: null);
    }

    /// <inheritdoc cref="BeginTransaction(IsolationLevel)"/>
    public async Task<IDbContextTransaction> BeginTransactionAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken = default)
    {
        context.RefuseToBeginInsideASave();
        return Follow(await RelationalDatabaseFacadeExtensions.BeginTransactionAsync(this, isolationLevel, cancellationToken), unit: null);
    }

    public override void CommitTransaction()
    {
        if (Followed is { } transaction)
        {
            transaction.Commit();
            return;
        }

        context.RefuseToEndATransactionInsideASave("committed");
        base.CommitTransaction();
    }

    public override Task CommitTransactionAsync(CancellationToken cancellationToken = default)
    {
        if (Followed is { } transaction)
            return transaction.CommitAsync(cancellationToken);

        context.RefuseToEndATransactionInsideASave("committed");
        return base.CommitTransactionAsync(cancellationToken);
    }

    /// <summary>
    /// Rolls back the transaction that is open. After a transaction begun here has committed, it does nothing, as the
    /// transaction's own rollback does: a commit whose after-commit step failed throws the
    /// <see cref="AfterCommitException"/> once the rows are committed, and the usual
    /// <c>try { commit } catch { rollback; throw; }</c> must not replace it with EF's "no active transaction". That
    /// holds also when an after-commit step saved, or began and ended a transaction of its own, before it failed. With
    /// no transaction begun here, or after one that was rolled back, it throws as EF does, also after a save for which
    /// the context opened and committed a transaction of its own.
    /// </summary>
    public override void RollbackTransaction()
    {
        if (Followed is { } transaction)
        {
            transaction.Rollback();
            return;
        }

        if (CommittedLast())
            return;

        context.RefuseToEndATransactionInsideASave("rolled back");
        base.RollbackTransaction();
    }

    /// <inheritdoc cref="RollbackTransaction"/>
    public override Task RollbackTransactionAsync(CancellationToken cancellationToken = default)
    {
        if (Followed is { } transaction)
            return transaction.RollbackAsync(cancellationToken);

        if (CommittedLast())
            return Task.CompletedTask;

        context.RefuseToEndATransactionInsideASave("rolled back");
        return base.RollbackTransactionAsync(cancellationToken);
    }

    // Whether the app's transaction that ended last committed, and EF holds no transaction since. A transaction EF holds
    // now was begun around this facade (UseTransaction), and a rollback is for that one.
    private bool CommittedLast()
    {
        if (lastEnded is not { Committed: true })
            return false;

        if (base.CurrentTransaction is null)
            return true;

        lastEnded = null;
        return false;
    }

    /// <summary>
    /// Takes a lock on a name and holds it until the open transaction ends, so that one transaction at a time does what
    /// the name stands for. Waits while another transaction holds it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For work that no unique index and no row version protects, such as giving out the next number of a series. Every
    /// piece of code that does the work takes the lock, under the same name. Code that does not take it is not held back,
    /// and another context of the same request that asks for the name waits like everybody else.
    /// </para>
    /// <para>
    /// <b>Read under the lock.</b> What the work depends on is read after the lock is taken, with a projection or
    /// <c>AsNoTracking</c>: a row the context has loaded before keeps the values it had then. A transaction with snapshot
    /// isolation reads what was committed before, so the call is refused in one.
    /// </para>
    /// <para>
    /// <b>The transaction.</b> One must be open. Its commit and its rollback release the lock, and on SQL Server so does a
    /// rollback to a savepoint that was set before the lock was taken. Nothing else does. A transaction may take a lock it
    /// holds again, at the cost of a round trip. Take it soon after the transaction begins, and keep the transaction
    /// short. A save handler takes its locks with <see cref="RowSet{TEntity}.TakeLockAsync"/>, which opens the save's
    /// transaction where that is needed.
    /// </para>
    /// <para>
    /// <b>Several locks.</b> Take them in one fixed order everywhere, for example sorted by name. Of two transactions
    /// that each hold a lock the other one waits for, the server ends the wait of one, which must be rolled back.
    /// </para>
    /// <para>
    /// <b>The wait,</b> on SQL Server, ends with a <see cref="TimeoutException"/> a little before the time a command of the
    /// context may take (<c>CommandTimeout</c>, 30 seconds unless it is set), and never where that is 0. A retrying
    /// execution strategy takes the exception for a passing fault, as it does a command's timeout.
    /// </para>
    /// <para>
    /// <b>Databases.</b> On SQL Server it is an application lock of exactly this name (<c>sp_getapplock</c>), so code
    /// that calls the procedure itself with the name is held back too. On SQLite it makes the transaction the one that can
    /// write, which holds back every other one that would, whatever name it asks for. Most wait where they begin, in
    /// SQLite's own way: on their thread, until the connection's timeout, with a <c>SqliteException</c> then. EF's
    /// in-memory provider has no transactions, and nothing is locked. On any other database the call throws.
    /// </para>
    /// <para>
    /// It is not a lease for a run of several transactions, such as an import that commits page by page: the first commit
    /// releases it.
    /// </para>
    /// </remarks>
    /// <param name="name">
    /// What is locked: a name of the app's own, up to 255 characters, such as <c>InvoiceNo:12</c>. Names are compared
    /// exactly, upper and lower case included.
    /// </param>
    /// <param name="cancellationToken">Ends the wait.</param>
    /// <exception cref="TimeoutException">
    /// The lock was not given: another transaction held it for too long, or the two waited for each other. Roll back.
    /// </exception>
    /// <exception cref="InvalidOperationException">No transaction is open in which the lock would protect anything.</exception>
    /// <exception cref="NotSupportedException">The database is neither SQL Server nor SQLite.</exception>
    public Task TakeLockAsync(string name, CancellationToken cancellationToken = default) => DatabaseLock.TakeAsync(context, name, cancellationToken);

    /// <summary>
    /// The wrapper of a transaction this context follows, including one it opened itself for a save. EF's own object
    /// for a transaction it cannot follow. Null when there is none.
    /// </summary>
    public override IDbContextTransaction? CurrentTransaction => Followed ?? base.CurrentTransaction;

    // Both settings are kept in EF's own facade: a pooled context snapshots them from there and restores them through
    // this class.
    public override AutoTransactionBehavior AutoTransactionBehavior
    {
        get => context.EfDatabase.AutoTransactionBehavior;
        set => context.EfDatabase.AutoTransactionBehavior = value;
    }

    public override bool AutoSavepointsEnabled
    {
        // EF asks this before a write inside a transaction. A save that set the framework's savepoint, or whose
        // failure rolls its whole transaction back, answers no for its own write.
        get => !HoldEfSavepoint && context.EfDatabase.AutoSavepointsEnabled;
        set => context.EfDatabase.AutoSavepointsEnabled = value;
    }

    /// <summary>Whether the write in progress must not get EF's own savepoint.</summary>
    internal bool HoldEfSavepoint { get; set; }

    /// <summary>The app's setting, whatever the write in progress answers EF.</summary>
    internal bool SavepointsAllowed => context.EfDatabase.AutoSavepointsEnabled;

    /// <summary>EF's own current transaction, which this context follows or not.</summary>
    internal IDbContextTransaction? EfCurrentTransaction => base.CurrentTransaction;

    /// <summary>The followed transaction that is open, or null.</summary>
    internal ShiftDbContextTransaction? Followed
    {
        get
        {
            context.ResetIfNewLease();

            // A relational transaction that EF no longer holds has ended without a word to its wrapper: UseTransaction
            // replaced it. A provider without transactions never holds one.
            if (followed is { Relational: true } transaction && !ReferenceEquals(base.CurrentTransaction, transaction.Inner))
                Abandon();

            return followed;
        }
    }

    /// <summary>Begins the transaction the context gives one save, on the save's own unit.</summary>
    internal async ValueTask<ShiftDbContextTransaction> BeginForSaveAsync(SaveUnit unit, bool async, CancellationToken cancellationToken) =>
        Follow(async ? await base.BeginTransactionAsync(cancellationToken) : base.BeginTransaction(), unit, own: true);

    internal void Ended(ShiftDbContextTransaction transaction)
    {
        if (!ReferenceEquals(followed, transaction))
            return;

        followed = null;

        if (!transaction.Own)
            lastEnded = transaction;
    }

    /// <summary>
    /// The commit of a transaction the app began has returned or thrown, after its after-commit stage. A rollback that
    /// follows is for that transaction, unless one is open now: a transaction that an after-commit step began and ended
    /// through this facade in the meantime does not take its place.
    /// </summary>
    internal void CommitEnded(ShiftDbContextTransaction transaction)
    {
        if (transaction.Own || followed is not null || base.CurrentTransaction is not null)
            return;

        lastEnded = transaction;
    }

    /// <summary>
    /// The context is going away, or was leased again, with a transaction still open, or EF holds another transaction
    /// now. EF rolls the open one back, so what waited for its commit is dropped.
    /// </summary>
    internal void Abandon()
    {
        var transaction = followed;
        followed = null;
        lastEnded = null;
        HoldEfSavepoint = false;
        transaction?.Abandon();
    }

    private ShiftDbContextTransaction Follow(IDbContextTransaction inner, SaveUnit? unit, bool own = false)
    {
        // Only a provider without transactions lets a second one begin.
        followed?.Abandon();

        if (!own)
            lastEnded = null;

        unit ??= new SaveUnit(context);
        var transaction = new ShiftDbContextTransaction(context, this, inner, unit, own);
        unit.Transaction = transaction;

        return followed = transaction;
    }
}
