using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace ShiftSoftware.ShiftEntity.Tests.Replication;

/// <summary>
/// A SQLite database in a file of its own, for one test host. Every context opens a connection of its own to it, as
/// every scope does on a server, so the syncs that a save starts in background tasks, several at once and each with a
/// context of its own, can read and write at the same time.
/// </summary>
/// <remarks>
/// <para>
/// One in-memory connection that every context shares cannot take that. A <see cref="SqliteConnection"/> must not be
/// used by two threads at once, and EF registers its SQL functions on a context's connection when the context first
/// uses it, which SQLite refuses while a command of another context is still running on that connection ("unable to
/// delete/modify user-function due to active statements"). A sync that ran into that failed, and its document was
/// missing.
/// </para>
/// <para>
/// The file is in WAL mode, so a sync that reads does not wait for one that writes, and every connection gets a busy
/// timeout, so a write that finds another write in progress waits for it instead of failing. Pooling is off, so a
/// connection closes as soon as its context is done with it and nothing holds the file open once the host is disposed.
/// The file is alone in a new folder under the temp folder, and the folder goes with everything SQLite put in it.
/// </para>
/// </remarks>
public sealed class SqliteTestFile : IAsyncDisposable
{
    private const int BusyTimeoutMilliseconds = 30_000;

    private readonly string folder = Path.Combine(Path.GetTempPath(), "shift-entity-sqlite-" + Guid.NewGuid().ToString("N"));

    public SqliteTestFile()
    {
        Directory.CreateDirectory(folder);
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(folder, "test.db"),
            Pooling = false,
        }.ToString();

        // Creates the file. The journal mode is kept in the file, so every later connection works in WAL mode.
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode = WAL;";
        command.ExecuteNonQuery();
    }

    public string ConnectionString { get; }

    /// <summary>Points a context's options at the file.</summary>
    public DbContextOptionsBuilder Use(DbContextOptionsBuilder options, Action<SqliteDbContextOptionsBuilder>? sqlite = null) =>
        options.UseSqlite(ConnectionString, sqlite).AddInterceptors(BusyTimeout.Instance);

    /// <inheritdoc cref="Use(DbContextOptionsBuilder, Action{SqliteDbContextOptionsBuilder}?)"/>
    public DbContextOptionsBuilder<TContext> Use<TContext>(DbContextOptionsBuilder<TContext> options) where TContext : DbContext =>
        options.UseSqlite(ConnectionString).AddInterceptors(BusyTimeout.Instance);

    /// <summary>Deletes the folder with the file. Every context that used it must be disposed first.</summary>
    public ValueTask DisposeAsync()
    {
        Directory.Delete(folder, recursive: true);
        return ValueTask.CompletedTask;
    }

    // SQLite's busy timeout is a setting of the connection, not of the file, so it is set on every connection a context
    // opens.
    private sealed class BusyTimeout : DbConnectionInterceptor
    {
        public static readonly BusyTimeout Instance = new();

        public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) => Set(connection);

        public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            Set(connection);
            return Task.CompletedTask;
        }

        private static void Set(DbConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA busy_timeout = {BusyTimeoutMilliseconds};";
            command.ExecuteNonQuery();
        }
    }
}
