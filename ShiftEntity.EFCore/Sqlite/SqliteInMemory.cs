using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ShiftSoftware.ShiftEntity.EFCore.Sqlite;

/// <summary>
/// What a <see cref="ShiftDbContext"/> on an in-memory SQLite database needs and EF does not do: keeping a shared
/// in-memory database alive between contexts, and saying when an in-memory database is not shared.
/// </summary>
/// <remarks>
/// <para>
/// A shared in-memory database (<c>Data Source=name;Mode=Memory;Cache=Shared</c>) exists only while a connection to it
/// is open. Each context opens and closes its own connection, so the database, and every row in it, would disappear
/// whenever no context happened to be using it. The first context on such a connection string opens one more
/// connection to it and keeps it open until the process ends, so the database lives as long as the app.
/// </para>
/// <para>
/// An in-memory database that is not shared (<c>Data Source=:memory:</c>, or <c>Mode=Memory</c> without
/// <c>Cache=Shared</c>) is a new, empty database for every connection, so rows saved through one context are gone in
/// the next. That is logged once as a warning rather than refused: a context used only for its model, or one context
/// that does all its work on one connection, works. A context the consumer gives an open connection
/// (<c>UseSqlite(connection)</c>) is left alone: that connection keeps the database alive.
/// </para>
/// <para>
/// Any other database is not touched.
/// </para>
/// </remarks>
internal static class SqliteInMemory
{
    // The SQLite provider's assembly, named by one of its public types: its options extension is in an internal
    // namespace, so the provider is recognised by the assembly it comes from, as EF's own Database.IsSqlite() does.
    private static readonly System.Reflection.Assembly SqliteProvider = typeof(SqliteDbContextOptionsBuilder).Assembly;

    private enum Kind { NotInMemory, Shared, NotShared }

    private static readonly ConcurrentDictionary<string, Kind> kinds = new();
    private static readonly ConcurrentDictionary<string, bool> warned = new();
    private static readonly Dictionary<string, SqliteConnection> keepAlive = new();
    private static readonly object keepAliveLock = new();

    /// <summary>Applies the in-memory support to a context's options, when they are SQLite on a connection string.</summary>
    internal static void Apply(DbContextOptions options, IServiceProvider? applicationServices)
    {
        var sqlite = options.Extensions.FirstOrDefault(x => x.Info.IsDatabaseProvider) is RelationalOptionsExtension provider &&
            provider.GetType().Assembly == SqliteProvider ? provider : null;

        if (sqlite is null || sqlite.Connection is not null || string.IsNullOrWhiteSpace(sqlite.ConnectionString))
            return;

        var connectionString = sqlite.ConnectionString;
        switch (kinds.GetOrAdd(connectionString, Classify))
        {
            case Kind.Shared:
                KeepAlive(connectionString);
                break;

            case Kind.NotShared:
                if (warned.TryAdd(connectionString, true))
                    applicationServices?.GetService<ILoggerFactory>()?.CreateLogger("ShiftSoftware.ShiftEntity.EFCore.Sqlite")
                        .LogWarning("The SQLite connection string '{ConnectionString}' is an in-memory database that is not shared: " +
                            "every connection gets a new, empty database, so rows saved through one DbContext are gone in the next. " +
                            "Use a named shared in-memory database instead, for example 'Data Source=MyApp;Mode=Memory;Cache=Shared', " +
                            "or give the DbContext an open connection with UseSqlite(connection).", connectionString);
                break;
        }
    }

    /// <summary>Whether a SQLite connection string is a file, a shared in-memory database or one that is not shared.</summary>
    private static Kind Classify(string connectionString)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        var dataSource = builder.DataSource;

        // A URI file name carries the same settings as query parameters, e.g. file:name?mode=memory&cache=shared.
        var uri = dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? dataSource : null;
        var inMemory = dataSource == ":memory:" || builder.Mode == SqliteOpenMode.Memory ||
            uri?.Contains("mode=memory", StringComparison.OrdinalIgnoreCase) == true || uri?.StartsWith("file::memory:", StringComparison.OrdinalIgnoreCase) == true;

        if (!inMemory)
            return Kind.NotInMemory;

        var shared = builder.Cache == SqliteCacheMode.Shared || uri?.Contains("cache=shared", StringComparison.OrdinalIgnoreCase) == true;

        return shared ? Kind.Shared : Kind.NotShared;
    }

    private static void KeepAlive(string connectionString)
    {
        lock (keepAliveLock)
        {
            if (keepAlive.ContainsKey(connectionString))
                return;

            var connection = new SqliteConnection(connectionString);
            connection.Open();
            keepAlive[connectionString] = connection;
        }
    }
}
