using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ShiftMapper;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.CosmosDbReplication;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.Model.Replication;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Replication;

/// <summary>
/// The model the replication pins save: items in categories, notes that are not replicated, and log rows that a host
/// trigger writes. The pins fix what replication does today, before its engine changes, so the same tests can prove
/// that the new engine does the same.
/// </summary>
public static class PinModel
{
    public sealed class Category : ShiftEntity<Category>, IShiftEntityReplication
    {
        public string Name { get; set; } = "";
        public DateTimeOffset? LastReplicationDate { get; set; }
        public string? LastReplicationStamp { get; set; }
    }

    public sealed class Item : ShiftEntity<Item>, IShiftEntityReplication
    {
        public string Code { get; set; } = "";
        public string Region { get; set; } = "";
        public int Rank { get; set; }
        public long? CategoryID { get; set; }
        public Category? Category { get; set; }
        public DateTimeOffset? LastReplicationDate { get; set; }
        public string? LastReplicationStamp { get; set; }
    }

    // Not replicated.
    public sealed class Note : ShiftEntity<Note>
    {
        public string Text { get; set; } = "";
    }

    // Replicated, and written by a host trigger from inside another row's save.
    public sealed class Log : ShiftEntity<Log>, IShiftEntityReplication
    {
        public string Text { get; set; } = "";
        public DateTimeOffset? LastReplicationDate { get; set; }
        public string? LastReplicationStamp { get; set; }
    }

    public sealed class Db(DbContextOptions<Db> options) : ShiftDbContext(options)
    {
        public DbSet<Category> Categories => Set<Category>();
        public DbSet<Item> Items => Set<Item>();
        public DbSet<Note> Notes => Set<Note>();
        public DbSet<Log> Logs => Set<Log>();
    }

    public sealed class ItemDocument
    {
        public string id { get; set; } = "";
        public string Region { get; set; } = "";
        public string ItemType { get; set; } = "Item";
        public int Rank { get; set; }
        public string Code { get; set; } = "";
        public string? CategoryID { get; set; }
        public string? CategoryName { get; set; }
        public CategoryReference? Category { get; set; }
    }

    public sealed class CategoryReference
    {
        public string id { get; set; } = "";
        public string Name { get; set; } = "";
    }

    public sealed class CategoryDocument
    {
        public string id { get; set; } = "";
        public string Name { get; set; } = "";
    }

    public sealed class LogDocument
    {
        public string id { get; set; } = "";
        public string Text { get; set; } = "";
    }

    public static ItemDocument Document(Item x) => new()
    {
        id = x.ID.ToString(),
        Region = x.Region,
        Rank = x.Rank,
        Code = x.Code,
        CategoryID = x.CategoryID?.ToString(),
        CategoryName = x.Category?.Name,
    };
}

/// <summary>
/// Reads the after-save trigger's own log messages. "Starting" is logged while the save runs; "Succeeded" or "Failed"
/// when the background task has finished, the bookkeeping write included. ShiftIdentity.Tests has a copy
/// (Infrastructure/ReplicationSyncLog.cs); keep the two the same.
/// </summary>
public sealed class ReplicationSyncLog : ILoggerProvider, ILogger
{
    private readonly ConcurrentQueue<string> started = new();
    private readonly ConcurrentQueue<string> failed = new();
    private readonly ConcurrentQueue<string> warnings = new();
    private readonly SemaphoreSlim finished = new(0);
    private int waitedFor;

    /// <summary>Each started sync, as "type id", in order.</summary>
    public IReadOnlyList<string> Started => started.ToList();

    /// <summary>The message of each failed sync.</summary>
    public IReadOnlyList<string> Failed => failed.ToList();

    /// <summary>Every warning, for example a catch-up run's report of failed rows.</summary>
    public IReadOnlyList<string> Warnings => warnings.ToList();

    /// <summary>Waits until every sync started so far has finished. It waits for the trigger's message, not for a set time.</summary>
    public async Task AllFinishedAsync()
    {
        while (waitedFor < started.Count)
        {
            Assert.True(await finished.WaitAsync(TimeSpan.FromSeconds(30)), "A replication sync did not finish.");
            waitedFor++;
        }
    }

    public ILogger CreateLogger(string categoryName) => this;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (logLevel == LogLevel.Warning)
            warnings.Enqueue(formatter(state, exception));

        if (state is not IReadOnlyList<KeyValuePair<string, object?>> values ||
            values.LastOrDefault().Value is not string format)
            return;

        if (format.StartsWith("CosmosDB Syncing is starting"))
        {
            var type = values.First(x => x.Key == "entityType").Value as Type;
            started.Enqueue($"{type?.Name} {values.First(x => x.Key == "entityID").Value}");
        }
        else if (format.StartsWith("CosmosDB Syncing Succeeded"))
            finished.Release();
        else if (format.StartsWith("CosmosDB Syncing Failed"))
        {
            failed.Enqueue(formatter(state, exception));
            finished.Release();
        }
    }

    public void Dispose() { }
}

/// <summary>
/// A ShiftMapper mapper double for the delegate-less registrations: it answers for the pairs a test gives it, the way
/// the host's generated mapper would.
/// </summary>
public sealed class PinMapper : IMapper
{
    private readonly Dictionary<(Type, Type), Func<object, object>> creates = new();
    private readonly Dictionary<(Type, Type), Func<object, object, object>> merges = new();

    public PinMapper Create<TSource, TDestination>(Func<TSource, TDestination> map)
    {
        creates[(typeof(TSource), typeof(TDestination))] = x => map((TSource)x)!;
        return this;
    }

    public PinMapper Merge<TSource, TDestination>(Func<TSource, TDestination, TDestination> map)
    {
        merges[(typeof(TSource), typeof(TDestination))] = (x, y) => map((TSource)x, (TDestination)y)!;
        return this;
    }

    public bool CanMap(Type source, Type destination) =>
        creates.ContainsKey((source, destination)) || merges.ContainsKey((source, destination));

    public TDestination Map<TDestination>(object source) => throw new NotSupportedException();

    public TDestination Map<TSource, TDestination>(TSource source) =>
        (TDestination)creates[(typeof(TSource), typeof(TDestination))](source!);

    public TDestination Map<TSource, TDestination>(TSource source, TDestination destination) =>
        (TDestination)merges[(typeof(TSource), typeof(TDestination))](source!, destination!);

    public IQueryable<TDestination> ProjectTo<TSource, TDestination>(IQueryable<TSource> source) => throw new NotSupportedException();

    public void Configure(ShiftMapperConfigurationSurface surface) => throw new NotSupportedException();
}

/// <summary>
/// One host for a replication pin: a SQLite database, the replication registration under test, a Cosmos DB stand-in
/// that records every write, and the trigger's log. Each test builds its own.
/// </summary>
public sealed class ReplicationPinHost : IAsyncDisposable
{
    public const string Database = "pins";

    private readonly SqliteConnection connection = new("DataSource=:memory:");

    public RecordingCosmos Cosmos { get; } = new(Database);
    public ReplicationSyncLog Log { get; } = new();
    public ServiceProvider Provider { get; }

    /// <param name="replication">The replication registration, as a host writes it inside
    /// <c>AddShiftEntityCosmosDbReplicationTrigger</c>. It gets the Cosmos stand-in's client.</param>
    /// <param name="services">More registrations: a mapper, a preparer, host triggers.</param>
    /// <param name="context">More context options, for example a host's own <c>UseTriggers</c> call.</param>
    /// <param name="containers">The containers the registration writes, with their partition key paths.</param>
    public ReplicationPinHost(
        Action<ShiftEntityCosmosDbOptions, Microsoft.Azure.Cosmos.CosmosClient>? replication,
        Action<IServiceCollection>? services = null,
        Action<DbContextOptionsBuilder>? context = null,
        params (string Id, string[] PartitionKeyPaths)[] containers)
    {
        foreach (var (id, paths) in containers)
            Cosmos.Container(id, paths);

        connection.Open();

        var collection = new ServiceCollection();
        collection.AddLogging(x => x.AddProvider(Log));
        collection.AddDbContext<PinModel.Db>(o =>
        {
            o.UseSqlite(connection);
            context?.Invoke(o);
        });

        if (replication is not null)
            collection.AddShiftEntityCosmosDbReplicationTrigger<PinModel.Db>(o => replication(o, Cosmos.Client));

        services?.Invoke(collection);
        Provider = collection.BuildServiceProvider(validateScopes: true);

        using var scope = Provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<PinModel.Db>().Database.EnsureCreated();
    }

    /// <summary>A context of its own, outside every scope: for setting up rows and for reading what was stored.</summary>
    public PinModel.Db Bare() => new(new DbContextOptionsBuilder<PinModel.Db>().UseSqlite(connection).Options);

    /// <summary>The stored row, read with a context of its own.</summary>
    public async Task<T> StoredAsync<T>(long id) where T : ShiftEntity<T>
    {
        await using var db = Bare();
        return await db.Set<T>().IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.ID == id);
    }

    /// <summary>Whether the row exists, read with a context of its own.</summary>
    public async Task<bool> ExistsAsync<T>(long id) where T : ShiftEntity<T>
    {
        await using var db = Bare();
        return await db.Set<T>().IgnoreQueryFilters().AnyAsync(x => x.ID == id);
    }

    public async ValueTask DisposeAsync()
    {
        await Log.AllFinishedAsync();   // no sync may still use the connection
        await Provider.DisposeAsync();
        Cosmos.Dispose();
        await connection.DisposeAsync();
    }
}
