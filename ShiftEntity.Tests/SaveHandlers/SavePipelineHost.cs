using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.Tests.Replication;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.SaveHandlers;

/// <summary>
/// The model the save handler and save lifecycle tests save: orders with lines and an owned address, a class derived
/// from an order, memos that handlers write, tickets that a handler numbers, and vouchers that a repository saves.
/// </summary>
public static class SavePipelineModel
{
    public interface INamed
    {
        string? Name { get; }
    }

    public class Address
    {
        public string? City { get; set; }
    }

    public class Order : ShiftEntity<Order>, INamed
    {
        public string? Name { get; set; }
        public string? Status { get; set; }
        public Address Address { get; set; } = new();
        public List<OrderLine> Lines { get; set; } = [];
    }

    /// <summary>A row of the orders table with a class of its own.</summary>
    public class RushOrder : Order
    {
        public string? Reason { get; set; }
    }

    public class OrderLine : ShiftEntity<OrderLine>
    {
        public long OrderID { get; set; }
        public Order? Order { get; set; }
        public string? Note { get; set; }
    }

    public class Memo : ShiftEntity<Memo>, INamed
    {
        public string? Name { get; set; }
    }

    /// <summary>A row that gets the next number of its queue when it is added.</summary>
    public class Ticket : ShiftEntity<Ticket>, INamed
    {
        public string Queue { get; set; } = "";
        public int Number { get; set; }
        public string? Name { get; set; }
    }

    /// <summary>
    /// A row a repository saves: it has a unique hash over its series and number, which the repository computes, and a
    /// memo that a repository can load with it.
    /// </summary>
    public class Voucher : ShiftEntity<Voucher>, INamed, ShiftSoftware.ShiftEntity.Core.Flags.IEntityHasUniqueHash<Voucher>
    {
        public string Series { get; set; } = "";
        public int Number { get; set; }
        public string? Name { get; set; }
        public long? MemoID { get; set; }
        public Memo? Memo { get; set; }

        public string? CalculateUniqueHash() => $"{Series}|{Number}";
    }

    public class Db(DbContextOptions<Db> options) : ShiftDbContext(options)
    {
        public DbSet<Order> Orders => Set<Order>();
        public DbSet<RushOrder> RushOrders => Set<RushOrder>();
        public DbSet<OrderLine> Lines => Set<OrderLine>();
        public DbSet<Memo> Memos => Set<Memo>();
        public DbSet<Ticket> Tickets => Set<Ticket>();
        public DbSet<Voucher> Vouchers => Set<Voucher>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Order>().OwnsOne(x => x.Address);
            base.OnModelCreating(modelBuilder);

            // The framework turns every cascade delete of a model off. This one is set after it: an order's lines are
            // deleted with their order.
            modelBuilder.Entity<OrderLine>().HasOne(x => x.Order).WithMany(x => x.Lines).OnDelete(DeleteBehavior.Cascade);
        }
    }
}

/// <summary>What the handlers and observers of one test saw, in order.</summary>
public sealed class SaveLog
{
    private readonly object gate = new();
    private readonly List<string> lines = [];

    public void Add(string line)
    {
        lock (gate)
            lines.Add(line);
    }

    public List<string> Lines
    {
        get
        {
            lock (gate)
                return [.. lines];
        }
    }

    public void Clear()
    {
        lock (gate)
            lines.Clear();
    }
}

/// <summary>
/// What a host's contexts sent to the database, as short lines: the transactions and savepoints EF reports, and the
/// statement and table of each command.
/// </summary>
public sealed partial class SqlTrace
{
    private readonly SaveLog lines = new();

    public List<string> Lines => lines.Lines;

    /// <summary>The lines without the reads, which differ between providers.</summary>
    public List<string> Writes => lines.Lines.Where(line => !line.StartsWith("SELECT")).ToList();

    public void Clear() => lines.Clear();

    public IInterceptor[] Interceptors => [new Commands(this), new Transactions(this)];

    [GeneratedRegex(@"\b(INSERT INTO|UPDATE|DELETE FROM|MERGE(?: INTO)?)\s+[\[""]?(\w+)", RegexOptions.IgnoreCase)]
    private static partial Regex Write();

    private void Command(DbCommand command)
    {
        var text = command.CommandText;
        var write = Write().Match(text);

        if (text.Contains("sp_getapplock", StringComparison.OrdinalIgnoreCase) || text.Contains("TakeLockAsync", StringComparison.Ordinal))
            lines.Add("LOCK");
        else if (write.Success)
            lines.Add($"{write.Groups[1].Value.Split(' ')[0].ToUpperInvariant()} {write.Groups[2].Value}");
        else if (text.Contains("SELECT", StringComparison.OrdinalIgnoreCase))
            lines.Add("SELECT");
    }

    private sealed class Commands(SqlTrace log) : DbCommandInterceptor
    {
        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            log.Command(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            log.Command(command);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            log.Command(command);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            log.Command(command);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        {
            log.Command(command);
            return result;
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            log.Command(command);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class Transactions(SqlTrace log) : DbTransactionInterceptor
    {
        public override DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
        {
            log.lines.Add("BEGIN");
            return result;
        }

        public override ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection, TransactionEndEventData eventData,
            DbTransaction result, CancellationToken cancellationToken = default)
        {
            log.lines.Add("BEGIN");
            return ValueTask.FromResult(result);
        }

        public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) => log.lines.Add("COMMIT");

        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            log.lines.Add("COMMIT");
            return Task.CompletedTask;
        }

        public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) => log.lines.Add("ROLLBACK");

        public override Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            log.lines.Add("ROLLBACK");
            return Task.CompletedTask;
        }

        public override void CreatedSavepoint(DbTransaction transaction, TransactionEventData eventData) => log.lines.Add("SAVEPOINT");

        public override Task CreatedSavepointAsync(DbTransaction transaction, TransactionEventData eventData, CancellationToken cancellationToken = default)
        {
            log.lines.Add("SAVEPOINT");
            return Task.CompletedTask;
        }

        public override void RolledBackToSavepoint(DbTransaction transaction, TransactionEventData eventData) => log.lines.Add("ROLLBACK TO SAVEPOINT");

        public override Task RolledBackToSavepointAsync(DbTransaction transaction, TransactionEventData eventData, CancellationToken cancellationToken = default)
        {
            log.lines.Add("ROLLBACK TO SAVEPOINT");
            return Task.CompletedTask;
        }
    }
}

/// <summary>Keeps what the framework logs, so a test can read a warning or an error it expects.</summary>
public sealed class RecordingLogs : ILoggerProvider
{
    private readonly List<(LogLevel Level, string Message)> entries = [];

    public List<(LogLevel Level, string Message)> Entries
    {
        get
        {
            lock (entries)
                return [.. entries];
        }
    }

    public ILogger CreateLogger(string categoryName) => new Recorder(this);

    public void Dispose() { }

    private sealed class Recorder(RecordingLogs logs) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            lock (logs.entries)
                logs.entries.Add((logLevel, formatter(state, exception)));
        }
    }
}

/// <summary>
/// A host with whatever services a test adds, on a database of its own: one in-memory SQLite connection, a SQLite file
/// that every context opens for itself, or a catalog on the local SQL Server.
/// </summary>
public sealed class SavePipelineHost : IAsyncDisposable
{
    private readonly SqliteConnection? connection;
    private readonly string? file;
    private readonly OwnedSqlCatalog? catalog;
    private readonly Action<DbContextOptionsBuilder> provider;

    private SavePipelineHost(ServiceProvider services, Action<DbContextOptionsBuilder> provider,
        SqliteConnection? connection = null, string? file = null, OwnedSqlCatalog? catalog = null)
    {
        Provider = services;
        this.provider = provider;
        this.connection = connection;
        this.file = file;
        this.catalog = catalog;
    }

    public ServiceProvider Provider { get; }
    public SaveLog Log => Provider.GetRequiredService<SaveLog>();
    public SqlTrace Sql => Provider.GetRequiredService<SqlTrace>();
    public RecordingLogs Logs => Provider.GetRequiredService<RecordingLogs>();
    public bool IsSqlServer => catalog is not null;

    /// <summary>On one in-memory SQLite connection, which every context of the host shares.</summary>
    /// <param name="services">More registrations.</param>
    /// <param name="context">More context options.</param>
    /// <param name="validateScopes">Whether a scoped service resolved from the root provider is refused.</param>
    /// <param name="sqlite">More SQLite options, for example an execution strategy.</param>
    public static SavePipelineHost Start(
        Action<IServiceCollection>? services = null,
        Action<DbContextOptionsBuilder>? context = null,
        bool validateScopes = true,
        Action<SqliteDbContextOptionsBuilder>? sqlite = null)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        return Build(options => options.UseSqlite(connection, sqlite), services, context, validateScopes,
            (provider, configure) => new(provider, configure, connection: connection),
            db => db.Database.EnsureCreated());
    }

    /// <summary>On a SQLite file, so that each context has a connection of its own and reads only what is committed.</summary>
    public static SavePipelineHost StartOnFile(Action<IServiceCollection>? services = null, Action<DbContextOptionsBuilder>? context = null)
    {
        var file = Path.Combine(Path.GetTempPath(), $"shift-entity-tests-{Guid.NewGuid():N}.db");

        return Build(options => options.UseSqlite($"Data Source={file}"), services, context, validateScopes: true,
            (provider, configure) => new(provider, configure, file: file),
            db => db.Database.EnsureCreated());
    }

    /// <summary>
    /// On a catalog of its own on the local SQL Server, which is dropped with the host. The test is skipped when no
    /// local SQL Server answers.
    /// </summary>
    /// <param name="name">Names the catalog.</param>
    /// <param name="connectionString">Changes the catalog's connection string, for example to switch MARS on.</param>
    public static async Task<SavePipelineHost> StartOnSqlServerAsync(
        string name,
        Action<IServiceCollection>? services = null,
        Action<DbContextOptionsBuilder>? context = null,
        Func<string, string>? connectionString = null)
    {
        var catalog = await OwnedSqlCatalog.CreateAsync(name, TestContext.Current.CancellationToken);
        var connect = connectionString?.Invoke(catalog.ConnectionString) ?? catalog.ConnectionString;

        return Build(options => options.UseSqlServer(connect), services, context, validateScopes: true,
            (provider, configure) => new(provider, configure, catalog: catalog),
            db => db.GetService<IRelationalDatabaseCreator>().CreateTables());
    }

    /// <summary>On SQL Server or on in-memory SQLite, for a test that must hold on both.</summary>
    public static async Task<SavePipelineHost> StartAsync(bool sqlServer, string name,
        Action<IServiceCollection>? services = null, Action<DbContextOptionsBuilder>? context = null) =>
        sqlServer ? await StartOnSqlServerAsync(name, services, context) : Start(services, context);

    private static SavePipelineHost Build(
        Action<DbContextOptionsBuilder> provider,
        Action<IServiceCollection>? services,
        Action<DbContextOptionsBuilder>? context,
        bool validateScopes,
        Func<ServiceProvider, Action<DbContextOptionsBuilder>, SavePipelineHost> create,
        Action<SavePipelineModel.Db> createTables)
    {
        var collection = new ServiceCollection();
        var sql = new SqlTrace();
        var logs = new RecordingLogs();

        void Configure(DbContextOptionsBuilder options)
        {
            provider(options);
            options.AddInterceptors(sql.Interceptors);
            context?.Invoke(options);
        }

        collection.AddSingleton<SaveLog>();
        collection.AddSingleton(sql);
        collection.AddSingleton(logs);
        collection.AddLogging(logging => logging.AddProvider(logs));
        collection.AddDbContext<SavePipelineModel.Db>(Configure);
        services?.Invoke(collection);

        var built = collection.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = validateScopes });

        using (var scope = built.CreateScope())
            createTables(scope.ServiceProvider.GetRequiredService<SavePipelineModel.Db>());

        sql.Clear();

        return create(built, Configure);
    }

    /// <summary>Runs one unit of work on a context of its own scope.</summary>
    public async Task<T> InScopeAsync<T>(Func<SavePipelineModel.Db, Task<T>> work)
    {
        await using var scope = Provider.CreateAsyncScope();
        return await work(scope.ServiceProvider.GetRequiredService<SavePipelineModel.Db>());
    }

    public Task InScopeAsync(Func<SavePipelineModel.Db, Task> work) => InScopeAsync(async db =>
    {
        await work(db);
        return 0;
    });

    /// <summary>
    /// A context without the host's services, so with no handler and no observer. On a file or on SQL Server it has a
    /// connection of its own, and so reads what is committed.
    /// </summary>
    public SavePipelineModel.Db Bare()
    {
        var options = new DbContextOptionsBuilder<SavePipelineModel.Db>();
        provider(options);
        return new SavePipelineModel.Db(options.Options);
    }

    public async ValueTask DisposeAsync()
    {
        await Provider.DisposeAsync();

        if (connection is not null)
            await connection.DisposeAsync();

        if (catalog is not null)
            await catalog.DisposeAsync();

        if (file is not null)
        {
            SqliteConnection.ClearAllPools();
            File.Delete(file);
        }
    }
}

/// <summary>Writes one line for each stage it is called in: the stage, and each row with how it changed.</summary>
public sealed class StageLog<TEntity>(SaveLog log)
    : IAfterSaveHandler<TEntity>, IBeforeCommitHandler<TEntity>, IAfterCommitHandler<TEntity> where TEntity : class
{
    public ValueTask AfterSaveAsync(SavedRows<TEntity> rows, CancellationToken cancellationToken) => Write("after save", rows);

    public ValueTask BeforeCommitAsync(SavedRows<TEntity> rows, CancellationToken cancellationToken) => Write("before commit", rows);

    public ValueTask AfterCommitAsync(SavedRows<TEntity> rows, CancellationToken cancellationToken) => Write("after commit", rows);

    private ValueTask Write(string stage, SavedRows<TEntity> rows)
    {
        log.Add($"{stage} of {typeof(TEntity).Name}: {Describe(rows)}");
        return ValueTask.CompletedTask;
    }

    public static string Describe(IEnumerable<ChangedRow<TEntity>> rows) =>
        string.Join(", ", rows.Select(row => $"{row.Change} {row.Entity.GetType().Name} '{(row.Entity as SavePipelineModel.INamed)?.Name}'"));
}

/// <summary>Writes one line once its rows are committed. It asks for no transaction.</summary>
public sealed class CommitLog<TEntity>(SaveLog log) : IAfterCommitHandler<TEntity> where TEntity : class
{
    public ValueTask AfterCommitAsync(SavedRows<TEntity> rows, CancellationToken cancellationToken)
    {
        log.Add($"after commit of {typeof(TEntity).Name}: {StageLog<TEntity>.Describe(rows)}");
        return ValueTask.CompletedTask;
    }
}

/// <summary>Writes what each order held before the save next to what it holds now.</summary>
[NeedsOriginal]
public sealed class OrderBeforeAndAfter(SaveLog log) : IAfterCommitHandler<SavePipelineModel.Order>
{
    public ValueTask AfterCommitAsync(SavedRows<SavePipelineModel.Order> rows, CancellationToken cancellationToken)
    {
        foreach (var row in rows)
            log.Add($"{row.Change}: '{row.Original?.Name}'/'{row.Original?.Status}' -> '{row.Entity.Name}'/'{row.Entity.Status}'");

        return ValueTask.CompletedTask;
    }
}

/// <summary>Writes each report it is given, as a part of the framework that observes the context's saves.</summary>
internal sealed class LoggingSaveObserver(SaveLog log) : IShiftDbContextSaveObserver
{
    public ValueTask SavedAsync(ShiftDbContext context, SaveReport report, CancellationToken cancellationToken)
    {
        log.Add("observer: " + string.Join(", ", report.Rows.Select(row => $"{row.Change} {row.EntityType.Name}")));
        return ValueTask.CompletedTask;
    }
}
