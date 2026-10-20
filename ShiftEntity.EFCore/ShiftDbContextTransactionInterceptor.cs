using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ShiftSoftware.ShiftEntity.EFCore;

/// <summary>
/// Tells a <see cref="ShiftDbContext"/> when it begins an explicit transaction and when that transaction ends, so the
/// work its saves asked to run after the commit (<see cref="ShiftDbContext.AfterCommit"/>) runs when the transaction
/// commits and is dropped when it rolls back. Added to the options with <c>UseShiftEntityAfterCommit()</c>.
/// </summary>
/// <remarks>
/// <para>
/// EF raises <c>TransactionCommitted</c> before it detaches the transaction from the context, so the work must not use
/// the context. The work that saves queue here only starts background tasks.
/// </para>
/// <para>
/// The work belongs to the transaction it was queued in, and only that transaction's commit runs it. A transaction
/// disposed without a commit or a rollback (the usual <c>using</c> block left by an exception) raises neither, so its
/// work is dropped when the context begins its next transaction. A transaction the context joined with
/// <c>UseTransaction</c> is not one it began, so work queued in it runs at once.
/// </para>
/// <para>
/// Savepoints are not followed: EF's own savepoint is rolled back only when a save fails, and a failed save queues
/// nothing. A host that rolls back to a savepoint of its own would still see the work of the saves after it run.
/// </para>
/// </remarks>
internal sealed class ShiftDbContextTransactionInterceptor : DbTransactionInterceptor
{
    public static readonly ShiftDbContextTransactionInterceptor Instance = new();

    private ShiftDbContextTransactionInterceptor() { }

    public override DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
    {
        (eventData.Context as ShiftDbContext)?.OnTransactionStarted(eventData.TransactionId);
        return result;
    }

    public override ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection, TransactionEndEventData eventData,
        DbTransaction result, CancellationToken cancellationToken = default)
    {
        (eventData.Context as ShiftDbContext)?.OnTransactionStarted(eventData.TransactionId);
        return ValueTask.FromResult(result);
    }

    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) =>
        (eventData.Context as ShiftDbContext)?.OnTransactionCommitted(eventData.TransactionId);

    public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        (eventData.Context as ShiftDbContext)?.OnTransactionCommitted(eventData.TransactionId);
        return Task.CompletedTask;
    }

    public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) =>
        (eventData.Context as ShiftDbContext)?.OnTransactionEnded();

    public override Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        (eventData.Context as ShiftDbContext)?.OnTransactionEnded();
        return Task.CompletedTask;
    }

    // A commit or a rollback that throws. Nothing is known to be committed, so the waiting work is dropped.
    public override void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData) =>
        (eventData.Context as ShiftDbContext)?.OnTransactionEnded();

    public override Task TransactionFailedAsync(DbTransaction transaction, TransactionErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        (eventData.Context as ShiftDbContext)?.OnTransactionEnded();
        return Task.CompletedTask;
    }
}
