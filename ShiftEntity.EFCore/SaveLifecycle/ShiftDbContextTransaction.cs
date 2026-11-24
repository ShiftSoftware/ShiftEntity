using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using System.Data.Common;

namespace ShiftSoftware.ShiftEntity.EFCore;

/// <summary>
/// A transaction a <see cref="ShiftDbContext"/> follows: EF's own transaction, wrapped so that the context knows how
/// it ends. Its commit runs the stages around EF's commit. Its rollback, and its disposal without a commit, drop what
/// waited for the commit.
/// </summary>
/// <remarks>
/// <para>
/// A rollback after a commit that succeeded does nothing. A commit throws the <see cref="AfterCommitException"/> once
/// EF has committed, and the usual <c>try { commit } catch { rollback; throw; }</c> would otherwise roll back a
/// finished transaction, which throws and hides the first exception.
/// </para>
/// <para>
/// <c>GetDbTransaction()</c> gives EF's own ADO.NET transaction, so code that works on the connection directly, such as
/// a bulk library, joins the transaction as before.
/// </para>
/// </remarks>
internal sealed class ShiftDbContextTransaction : IDbContextTransaction, IInfrastructure<DbTransaction>
{
    private enum Outcome
    {
        Open,
        Committed,
        Ended,
    }

    private readonly ShiftDbContext context;
    private readonly ShiftDatabaseFacade facade;
    private readonly bool tellsWhenItEnds;
    private Outcome outcome;

    public ShiftDbContextTransaction(ShiftDbContext context, ShiftDatabaseFacade facade, IDbContextTransaction inner, SaveUnit unit, bool own)
    {
        this.context = context;
        this.facade = facade;
        Inner = inner;
        Unit = unit;
        Own = own;

        // An ADO.NET transaction has a connection while it is open, and none once it has completed. A provider that
        // shows none from the start cannot say when its transaction ended.
        tellsWhenItEnds = ConnectionOf(inner) is not null;
    }

    /// <summary>EF's own transaction.</summary>
    public IDbContextTransaction Inner { get; }

    /// <summary>The saves made in the transaction, and what waits for its commit.</summary>
    public SaveUnit Unit { get; }

    /// <summary>Whether the context opened it for one save, which also commits it.</summary>
    public bool Own { get; }

    /// <summary>Whether EF holds it as its current transaction while it is open. A provider without transactions doesn't.</summary>
    public bool Relational => Inner is IInfrastructure<DbTransaction>;

    /// <summary>Whether EF's commit of it succeeded. A rollback after that does nothing.</summary>
    public bool Committed => outcome == Outcome.Committed;

    public Guid TransactionId => Inner.TransactionId;

    public bool SupportsSavepoints => Inner.SupportsSavepoints;

    /// <summary>
    /// Whether the ADO.NET transaction has completed while EF still holds it: the server rolled it back (a deadlock
    /// victim, an aborted batch), or the app ended it through the ADO.NET object.
    /// </summary>
    public bool EndedOutsideEf => outcome == Outcome.Open && tellsWhenItEnds && ConnectionOf(Inner) is null;

    private static DbConnection? ConnectionOf(IDbContextTransaction transaction)
    {
        try
        {
            return transaction is IInfrastructure<DbTransaction> relational ? relational.Instance.Connection : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    DbTransaction IInfrastructure<DbTransaction>.Instance => Inner.GetDbTransaction();

    // ── Commit ──────────────────────────────────────────────────────────────────

    public void Commit() => SaveLifecycleSync.Wait(() => CommitCoreAsync(async: false, fromItsSave: false, default));

    public Task CommitAsync(CancellationToken cancellationToken = default) => CommitCoreAsync(async: true, fromItsSave: false, cancellationToken);

    /// <summary>The commit of a transaction the context opened for a save, by that save.</summary>
    public Task CommitForSaveAsync(bool async, CancellationToken cancellationToken) => CommitCoreAsync(async, fromItsSave: true, cancellationToken);

    private async Task CommitCoreAsync(bool async, bool fromItsSave, CancellationToken cancellationToken)
    {
        if (!fromItsSave)
            context.RefuseInsideItsSave(this, "committed");

        // Committed or ended already: EF says so, as it always did.
        if (outcome != Outcome.Open)
        {
            if (async)
                await Inner.CommitAsync(cancellationToken);
            else
                Inner.Commit();

            return;
        }

        RefuseWhenRollbackOnly();

        await Unit.RunBeforeCommitAsync(cancellationToken);

        // A save that a before-commit handler made may have failed without a way to undo it alone.
        RefuseWhenRollbackOnly();

        try
        {
            if (async)
                await Inner.CommitAsync(cancellationToken);
            else
                Inner.Commit();
        }
        catch
        {
            // The server may have committed. Nothing more is known, so what waited is dropped, and said.
            End(Ending.Unknown, "its commit failed, so it is not known whether the rows were committed");
            throw;
        }

        outcome = Outcome.Committed;
        facade.Ended(this);

        try
        {
            await Unit.CompleteAfterCommitAsync(Unit.EnterAfterCommit());
        }
        finally
        {
            // An after-commit step may have begun and ended a transaction of its own through Database. A rollback the
            // caller sends after this commit, as in try { commit } catch { rollback; throw; }, is for this transaction.
            facade.CommitEnded(this);
        }
    }

    private void RefuseWhenRollbackOnly()
    {
        if (Unit.RollbackOnly is { } reason)
            throw new InvalidOperationException($"This transaction of {context.GetType().Name} can only be rolled back: {reason}");
    }

    // ── Rollback ────────────────────────────────────────────────────────────────

    public void Rollback() => Rollback(fromItsSave: false);

    /// <summary>The rollback of a transaction the context opened for a save, by that save.</summary>
    public void Rollback(bool fromItsSave)
    {
        if (!fromItsSave)
            context.RefuseInsideItsSave(this, "rolled back");

        if (outcome == Outcome.Committed)
            return;

        if (outcome != Outcome.Open)
        {
            Inner.Rollback();
            return;
        }

        try
        {
            Inner.Rollback();
        }
        finally
        {
            End(Ending.RolledBack);
        }
    }

    public Task RollbackAsync(CancellationToken cancellationToken = default) => RollbackAsync(fromItsSave: false, cancellationToken);

    /// <inheritdoc cref="Rollback(bool)"/>
    public async Task RollbackAsync(bool fromItsSave, CancellationToken cancellationToken)
    {
        if (!fromItsSave)
            context.RefuseInsideItsSave(this, "rolled back");

        if (outcome == Outcome.Committed)
            return;

        if (outcome != Outcome.Open)
        {
            await Inner.RollbackAsync(cancellationToken);
            return;
        }

        try
        {
            await Inner.RollbackAsync(cancellationToken);
        }
        finally
        {
            End(Ending.RolledBack);
        }
    }

    // ── Savepoints ──────────────────────────────────────────────────────────────

    public void CreateSavepoint(string name)
    {
        Inner.CreateSavepoint(name);
        Unit.MarkSavepoint(name);
    }

    public async Task CreateSavepointAsync(string name, CancellationToken cancellationToken = default)
    {
        await Inner.CreateSavepointAsync(name, cancellationToken);
        Unit.MarkSavepoint(name);
    }

    public void RollbackToSavepoint(string name)
    {
        context.RefuseToRollBackPastItsSave(this, name);
        Inner.RollbackToSavepoint(name);
        context.RememberUndone(Unit.RollbackToSavepoint(name));
    }

    public async Task RollbackToSavepointAsync(string name, CancellationToken cancellationToken = default)
    {
        context.RefuseToRollBackPastItsSave(this, name);
        await Inner.RollbackToSavepointAsync(name, cancellationToken);
        context.RememberUndone(Unit.RollbackToSavepoint(name));
    }

    public void ReleaseSavepoint(string name)
    {
        Inner.ReleaseSavepoint(name);
        Unit.ReleaseSavepoint(name);
    }

    public async Task ReleaseSavepointAsync(string name, CancellationToken cancellationToken = default)
    {
        await Inner.ReleaseSavepointAsync(name, cancellationToken);
        Unit.ReleaseSavepoint(name);
    }

    // ── The end ─────────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (outcome != Outcome.Open)
        {
            Inner.Dispose();
            return;
        }

        // Disposing an open transaction rolls it back, and a save that is still running in it would carry on without one.
        context.RefuseInsideItsSave(this, "disposed");

        var ending = EndingWithoutACommit();

        try
        {
            Inner.Dispose();
        }
        finally
        {
            End(ending, "it had ended outside EF when it was disposed, so it is not known whether the rows were committed");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (outcome != Outcome.Open)
        {
            await Inner.DisposeAsync();
            return;
        }

        context.RefuseInsideItsSave(this, "disposed");

        var ending = EndingWithoutACommit();

        try
        {
            await Inner.DisposeAsync();
        }
        finally
        {
            End(ending, "it had ended outside EF when it was disposed, so it is not known whether the rows were committed");
        }
    }

    /// <summary>The context was disposed or leased again, or the transaction was replaced, while it was open.</summary>
    public void Abandon()
    {
        if (outcome == Outcome.Open)
            End(Ending.Abandoned, "the context was disposed, returned to its pool or given another transaction while it was open, so it was rolled back");
    }

    private enum Ending
    {
        /// <summary>Rolled back. The context still shows the rows as saved, so it remembers one of them.</summary>
        RolledBack,

        /// <summary>Committed or not, nobody here can tell. What waited is dropped and an error says so.</summary>
        Unknown,

        /// <summary>Rolled back with the context, which is gone. A warning says what was dropped.</summary>
        Abandoned,
    }

    // Disposing an open transaction rolls it back. When its ADO.NET transaction has already completed and EF was never
    // asked to commit or roll back, something else ended it: the app through the ADO.NET object, or the server. A unit
    // that could only be rolled back was not committed by anyone who asked this context.
    private Ending EndingWithoutACommit() =>
        EndedOutsideEf && Unit.RollbackOnly is null ? Ending.Unknown : Ending.RolledBack;

    private void End(Ending ending, string? why = null)
    {
        outcome = Outcome.Ended;
        facade.Ended(this);

        var (rows, work) = Unit.Waiting();
        var written = Unit.Drop();

        if (ending == Ending.RolledBack)
        {
            context.RememberUndone(written);
            return;
        }

        if (rows + work == 0)
            return;

        context.LifecycleLogger?.Log(
            ending == Ending.Unknown ? LogLevel.Error : LogLevel.Warning,
            "A transaction of {Context} ended without a commit this context could follow: {Why}. What its saves had waiting for the commit was dropped: " +
            "{Work} piece(s) of work, and the after-commit handlers of {Rows} row(s).",
            context.GetType().Name, why, work, rows);
    }
}

/// <summary>The synchronous entry points of the lifecycle, which share the asynchronous path.</summary>
internal static class SaveLifecycleSync
{
    /// <summary>
    /// Waits for work that may run handlers. The caller's synchronization context is cleared meanwhile, so a handler
    /// that awaits without <c>ConfigureAwait(false)</c> carries on on the thread pool and cannot deadlock against the
    /// thread that waits here.
    /// </summary>
    public static void Wait(Func<Task> work)
    {
        var captured = SynchronizationContext.Current;

        if (captured is null)
        {
            work().GetAwaiter().GetResult();
            return;
        }

        SynchronizationContext.SetSynchronizationContext(null);

        try
        {
            work().GetAwaiter().GetResult();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(captured);
        }
    }
}
