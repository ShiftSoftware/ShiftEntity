using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ShiftSoftware.ShiftEntity.CosmosDbReplication;
using ShiftSoftware.ShiftEntity.CosmosDbReplication.Services;
using static ShiftSoftware.ShiftEntity.Tests.Replication.TreeModel;

namespace ShiftSoftware.ShiftEntity.Tests.Replication;

/// <summary>
/// Records what each context sends to the database: the text of every command, and the transactions and savepoints it
/// begins, commits and rolls back, in order.
/// </summary>
public sealed class SqlLog : DbCommandInterceptor, IDbTransactionInterceptor
{
    private readonly ConcurrentDictionary<DbContext, ConcurrentQueue<string>> sent = new();

    /// <summary>Everything one context sent, in order: command texts and BEGIN, COMMIT, ROLLBACK and SAVEPOINT.</summary>
    public IReadOnlyList<string> SentBy(DbContext context) => sent.TryGetValue(context, out var texts) ? texts.ToList() : [];

    /// <summary>Everything every context sent, in no defined order between contexts.</summary>
    public IReadOnlyList<string> All => sent.Values.SelectMany(x => x).ToList();

    public void Clear() => sent.Clear();

    private void Record(DbContext? context, string text)
    {
        if (context is not null)
            sent.GetOrAdd(context, _ => new()).Enqueue(text);
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Record(eventData.Context, command.CommandText);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Record(eventData.Context, command.CommandText);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Record(eventData.Context, command.CommandText);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Record(eventData.Context, command.CommandText);
        return ValueTask.FromResult(result);
    }

    public DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
    {
        Record(eventData.Context, "BEGIN");
        return result;
    }

    public ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection, TransactionEndEventData eventData, DbTransaction result,
        CancellationToken cancellationToken = default)
    {
        Record(eventData.Context, "BEGIN");
        return ValueTask.FromResult(result);
    }

    public void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) => Record(eventData.Context, "COMMIT");

    public Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Record(eventData.Context, "COMMIT");
        return Task.CompletedTask;
    }

    public void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) => Record(eventData.Context, "ROLLBACK");

    public Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Record(eventData.Context, "ROLLBACK");
        return Task.CompletedTask;
    }

    public void CreatedSavepoint(DbTransaction transaction, TransactionEventData eventData) => Record(eventData.Context, "SAVEPOINT");

    public Task CreatedSavepointAsync(DbTransaction transaction, TransactionEventData eventData, CancellationToken cancellationToken = default)
    {
        Record(eventData.Context, "SAVEPOINT");
        return Task.CompletedTask;
    }
}

/// <summary>
/// One host of the shop tree (<see cref="TreeModel"/>) on SQLite: the tree-only shop registration, a Cosmos DB stand-in
/// with the <c>shops</c> and <c>owners</c> containers, the sync log and an <see cref="SqlLog"/>. Each test builds its
/// own.
/// </summary>
public sealed class ShopTreeHost : IAsyncDisposable
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    // In memory, one connection that every context shares. On disk, a file each context opens a connection of its
    // own to, for tests that run many syncs at once.
    private readonly SqliteConnection connection = new("DataSource=:memory:");
    private readonly string? file;

    public RecordingCosmos Cosmos { get; } = new RecordingCosmos(ReplicationPinHost.Database)
        .Container("shops", "/id")
        .Container("owners", "/id");

    public ReplicationSyncLog Log { get; } = new();
    public SqlLog Sql { get; } = new();
    public ServiceProvider Provider { get; }

    /// <param name="replication">Whether the process registers the shop's save-time replication.</param>
    /// <param name="services">More registrations.</param>
    /// <param name="context">More context options.</param>
    /// <param name="sqlite">More SQLite options, for example an execution strategy.</param>
    /// <param name="onDisk">A database file instead of one shared in-memory connection.</param>
    public ShopTreeHost(bool replication = true, Action<IServiceCollection>? services = null,
        Action<DbContextOptionsBuilder>? context = null, Action<SqliteDbContextOptionsBuilder>? sqlite = null, bool onDisk = false)
    {
        if (onDisk)
            file = Path.Combine(Path.GetTempPath(), "shop-tree-" + Guid.NewGuid().ToString("N") + ".db");
        else
            connection.Open();

        var collection = new ServiceCollection();
        collection.AddLogging(x => x.AddProvider(Log));
        collection.AddDbContext<Db>(o =>
        {
            UseDatabase(o, sqlite).AddInterceptors(Sql);
            context?.Invoke(o);
        });

        if (replication)
            collection.AddShiftEntityCosmosDbReplicationTrigger<Db>(o =>
                o.SetUpReplication<Db, Shop>(Cosmos.Client, ReplicationPinHost.Database)
                    .ReplicateTree<ShopDocument>("shops", x => x.id, x => Document(x.Entity)));
        collection.AddShiftEntityCosmosDbReplication<Db>();

        services?.Invoke(collection);
        Provider = collection.BuildServiceProvider(validateScopes: true);

        using var db = Bare();
        db.Database.EnsureCreated();
        if (file is not null)
            db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL");
    }

    private DbContextOptionsBuilder UseDatabase(DbContextOptionsBuilder options, Action<SqliteDbContextOptionsBuilder>? sqlite = null) =>
        file is null ? options.UseSqlite(connection, sqlite) : options.UseSqlite($"Data Source={file};Pooling=False", sqlite);

    /// <summary>A context of its own, outside every scope and without replication.</summary>
    public Db Bare() => new((DbContextOptions<Db>)UseDatabase(new DbContextOptionsBuilder<Db>()).Options);

    /// <summary>
    /// Two lands, districts and towns, two owners and three products; shop 1 in town 1 of owner 1, shop 2 in town 2 of
    /// owner 2, shop 3 in town 1 of owner 2. Written without replication, then each shop recorded as replicated.
    /// </summary>
    public async Task SeedAsync()
    {
        await using var db = Bare();
        var lands = new[] { new Land { Name = "North" }, new Land { Name = "South" } };
        var districts = new[] { new District { Name = "Hills", Land = lands[0] }, new District { Name = "Coast", Land = lands[1] } };
        var towns = new[] { new Town { Name = "Oak", District = districts[0] }, new Town { Name = "Pier", District = districts[1] } };
        var owners = new[] { new Owner { Name = "Ada" }, new Owner { Name = "Bo" } };
        var products = new[] { new Product { Name = "Bread" }, new Product { Name = "Milk" }, new Product { Name = "Tea" } };
        db.AddRange(lands);
        db.AddRange(districts);
        db.AddRange(towns);
        db.AddRange(owners);
        db.AddRange(products);
        db.Shops.AddRange(
            new Shop { Name = "Corner", Town = towns[0], Owner = owners[0] },
            new Shop { Name = "Dock", Town = towns[1], Owner = owners[1] },
            new Shop { Name = "Mill", Town = towns[0], Owner = owners[1] });
        await db.SaveChangesAsync(Ct);
        await db.Shops.ExecuteUpdateAsync(s => s.SetProperty(x => x.LastReplicationDate, x => (DateTimeOffset?)x.LastSaveDate), Ct);
    }

    /// <summary>Changes rows in a scope of the host and saves them, then waits for the syncs the save started.</summary>
    public async Task SaveAsync(Func<Db, Task> change)
    {
        await using (var scope = Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Db>();
            await change(db);
            await db.SaveChangesAsync(Ct);
        }
        await Log.AllFinishedAsync();
    }

    public async Task<Shop> StoredAsync(long id)
    {
        await using var db = Bare();
        return await db.Shops.AsNoTracking().SingleAsync(x => x.ID == id, Ct);
    }

    /// <summary>The shops marked as not replicated, by ID, as "[1,3]".</summary>
    public async Task<string> MarkedAsync()
    {
        await using var db = Bare();
        var ids = await db.Shops.AsNoTracking().Where(x => x.LastReplicationDate == null).Select(x => x.ID).OrderBy(x => x).ToListAsync(Ct);
        return "[" + string.Join(",", ids) + "]";
    }

    /// <summary>A catch-up of the shops' tree documents, in a scope of the host.</summary>
    public async Task<CosmosDbReplicationResult> CatchUpAsync(bool updateAll = false, Func<Db, Task>? before = null)
    {
        await using var scope = Provider.CreateAsyncScope();
        if (before is not null)
            await before(scope.ServiceProvider.GetRequiredService<Db>());

        return await scope.ServiceProvider.GetRequiredService<CosmosDBReplication>()
            .SetUp<Db, Shop>(Cosmos.Client, ReplicationPinHost.Database)
            .ReplicateTree<ShopDocument>("shops", Document)
            .RunAndReportAsync(updateAll);
    }

    public IReadOnlyList<string> Shops => Cosmos.StoredLines("shops");

    public async ValueTask DisposeAsync()
    {
        await Log.AllFinishedAsync();   // no sync may still use the connection
        await Provider.DisposeAsync();
        Cosmos.Dispose();
        await connection.DisposeAsync();
        if (file is not null)
            foreach (var path in new[] { file, file + "-wal", file + "-shm" })
                File.Delete(path);
    }
}
