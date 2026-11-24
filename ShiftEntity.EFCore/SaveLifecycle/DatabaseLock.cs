using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using System.Data;
using System.Data.Common;

namespace ShiftSoftware.ShiftEntity.EFCore;

/// <summary>
/// A lock on a name that a transaction holds until it ends: what <see cref="ShiftDatabaseFacade.TakeLockAsync"/> and
/// <see cref="RowSet{TEntity}.TakeLockAsync"/> take.
/// </summary>
/// <remarks>
/// It awaits without the caller's synchronization context: there is no synchronous form, and code that has to wait for
/// it on a thread must not hang there with the lock in its hand.
/// </remarks>
internal static class DatabaseLock
{
    // The longest name sp_getapplock tells apart. It cuts a longer one off without a word, and two names could be one.
    private const int LongestName = 255;

    // EF's names for the providers, which is what its own IsSqlServer() and IsSqlite() compare.
    private const string SqlServer = "Microsoft.EntityFrameworkCore.SqlServer";
    private const string Sqlite = "Microsoft.EntityFrameworkCore.Sqlite";
    private const string InMemory = "Microsoft.EntityFrameworkCore.InMemory";

    public static void CheckName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (name.Length > LongestName)
            throw new ArgumentException($"The name of a lock can have {LongestName} characters, and this one has {name.Length}.", nameof(name));
    }

    public static Task TakeAsync(ShiftDbContext db, string name, CancellationToken cancellationToken)
    {
        CheckName(name);

        return TakeAsync(db, name, db.Database.ProviderName, cancellationToken);
    }

    /// <summary>The lock as it is taken with the named provider.</summary>
    internal static Task TakeAsync(ShiftDbContext db, string name, string? provider, CancellationToken cancellationToken) => provider switch
    {
        SqlServer => TakeOnSqlServerAsync(db, name, cancellationToken),
        Sqlite => TakeOnSqliteAsync(db, name, cancellationToken),

        // It has no transactions, so there is nothing to lock with. It is for tests, in one process.
        InMemory => Task.CompletedTask,

        // Not locking, and not saying so, would let through what the caller wants held back.
        _ => throw new NotSupportedException(
            $"{db.GetType().Name} cannot take the lock '{name}' (TakeLockAsync): its database provider is {provider}, and the framework knows " +
            "how to take one on SQL Server and on SQLite only."),
    };

    // ── SQL Server ──────────────────────────────────────────────────────────────

    // An application lock, which belongs to the transaction and is released with it. It is on the name and not on rows,
    // so it needs no index and no row to lock. A range lock (UPDLOCK, HOLDLOCK) on the query that reads the last number is
    // taken key by key: the first saves of an empty series all wait at the end of the index, and the one that gets it can
    // deadlock with a later save that read the new rows first.
    private static async Task TakeOnSqlServerAsync(ShiftDbContext db, string name, CancellationToken cancellationToken)
    {
        // Such a transaction reads what was committed when it began to read, so it may not see what the last transaction
        // that held the lock committed. The lock would be given, and the next number would be the last one again.
        if (RequireATransaction(db, name) == IsolationLevel.Snapshot)
            throw new InvalidOperationException(
                $"{db.GetType().Name} cannot take the lock '{name}' (TakeLockAsync) in a transaction with snapshot isolation: what it reads under " +
                "the lock may be older than what the last transaction that held the lock committed. Begin it with another isolation level.");

        var wait = MillisecondsToWait(db.Database);
        int answer;

        try
        {
            answer = (await db.Database.SqlQueryRaw<int>(
                "DECLARE @answer int; " +
                "EXEC @answer = sys.sp_getapplock @Resource = {0}, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = {1}; " +
                "SELECT @answer AS [Value];",
                name, wait).ToListAsync(cancellationToken).ConfigureAwait(false))[0];
        }
        catch (DbException cut) when (cancellationToken.IsCancellationRequested)
        {
            // SqlClient reports a command that was cancelled while it ran as one that failed.
            throw new OperationCanceledException($"The wait for the lock '{name}' was cancelled.", cut, cancellationToken);
        }

        if (Refusal(db, name, wait, answer) is { } refusal)
            throw refusal;

        // The transaction may have ended while it waited: an ambient one times out by itself.
        RequireATransaction(db, name);
    }

    /// <summary>What an answer of sp_getapplock is told the caller as. Null when the lock was given, at once or after a wait.</summary>
    internal static Exception? Refusal(ShiftDbContext db, string name, int wait, int answer) => answer switch
    {
        >= 0 => null,

        -1 => new TimeoutException(
            $"The lock '{name}' could not be taken within {wait / 1000d:0.#} s, because another transaction holds it. The wait ends a little " +
            $"before the command timeout of {db.GetType().Name}."),

        -3 => new TimeoutException(
            $"The lock '{name}' could not be taken: this transaction and another one each hold something the other one waits for, and the " +
            "server ended this one's wait. Roll this transaction back, because the other one waits until then. Take locks in one fixed order " +
            "everywhere, for example sorted by their names."),

        _ => new InvalidOperationException($"The lock '{name}' could not be taken: sp_getapplock answered {answer}."),
    };

    // A little less than a command may take, so that a wait that is too long ends with an answer and not with a command
    // that is cut off. Where a command may take for ever, so may the wait.
    internal static int MillisecondsToWait(DatabaseFacade database)
    {
        long seconds = database.GetCommandTimeout() ?? CommandTimeoutOfTheConnection(database);

        return seconds == 0 ? -1 : (int)Math.Min(int.MaxValue, Math.Max(seconds * 1000 - 1000, seconds * 500));
    }

    private static int CommandTimeoutOfTheConnection(DatabaseFacade database)
    {
        using var command = database.GetDbConnection().CreateCommand();
        return command.CommandTimeout;
    }

    // ── SQLite ──────────────────────────────────────────────────────────────────

    // One transaction at a time can write to a SQLite database, so the one that can is the one that holds the lock,
    // whatever its name. EF begins its transactions as writers (BEGIN IMMEDIATE), but not every one: one begun with
    // ReadUncommitted, or joined with UseTransaction, may have begun as a reader, and two readers may both be in one.
    // A write makes any of them the writer, also one that changes nothing.
    private static async Task TakeOnSqliteAsync(ShiftDbContext db, string name, CancellationToken cancellationToken)
    {
        RequireATransaction(db, name);

        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException(
                $"{db.GetType().Name} cannot take the lock '{name}' (TakeLockAsync) in an ambient transaction (TransactionScope): SQLite takes no " +
                "part in one, although EF reports it. Begin the transaction through Database.");

        await db.Database.ExecuteSqlRawAsync(AWriteThatChangesNothing(db), cancellationToken).ConfigureAwait(false);
    }

    private static string AWriteThatChangesNothing(ShiftDbContext db)
    {
        var table = db.Model.GetEntityTypes().Select(type => type.GetTableName()).FirstOrDefault(name => name is not null)
            ?? throw new InvalidOperationException($"{db.GetType().Name} has no table, so on SQLite it has nothing to take a lock with (TakeLockAsync).");

        return $"DELETE FROM {db.GetService<ISqlGenerationHelper>().DelimitIdentifier(table)} WHERE 0; -- TakeLockAsync";
    }

    // ── The transaction ─────────────────────────────────────────────────────────

    private static IsolationLevel RequireATransaction(ShiftDbContext db, string name) =>
        db.IsolationOfTheOpenTransaction() ?? throw new InvalidOperationException(
            $"{db.GetType().Name} cannot take the lock '{name}' (TakeLockAsync): no transaction is open, or the one there was has ended. A " +
            "transaction holds the lock until it ends, so begin one first, through Database. A before-save handler takes its lock with " +
            "rows.TakeLockAsync, which opens the save's transaction.");
}
