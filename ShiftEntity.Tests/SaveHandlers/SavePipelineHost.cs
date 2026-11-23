using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.EFCore;

namespace ShiftSoftware.ShiftEntity.Tests.SaveHandlers;

/// <summary>
/// The model the save handler tests save: orders with lines and an owned address, a class derived from an order, and
/// memos that handlers write.
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

    public class Db(DbContextOptions<Db> options) : ShiftDbContext(options)
    {
        public DbSet<Order> Orders => Set<Order>();
        public DbSet<RushOrder> RushOrders => Set<RushOrder>();
        public DbSet<OrderLine> Lines => Set<OrderLine>();
        public DbSet<Memo> Memos => Set<Memo>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Order>().OwnsOne(x => x.Address);
            base.OnModelCreating(modelBuilder);
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

/// <summary>A host on one in-memory SQLite database, with whatever services a test adds.</summary>
public sealed class SavePipelineHost : IAsyncDisposable
{
    private readonly SqliteConnection connection;

    private SavePipelineHost(ServiceProvider provider, SqliteConnection connection)
    {
        Provider = provider;
        this.connection = connection;
    }

    public ServiceProvider Provider { get; }
    public SaveLog Log => Provider.GetRequiredService<SaveLog>();

    public static SavePipelineHost Start(
        Action<IServiceCollection>? services = null,
        Action<DbContextOptionsBuilder>? context = null,
        bool validateScopes = true)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var collection = new ServiceCollection();
        collection.AddSingleton<SaveLog>();
        collection.AddDbContext<SavePipelineModel.Db>(options =>
        {
            options.UseSqlite(connection);
            context?.Invoke(options);
        });
        services?.Invoke(collection);

        var provider = collection.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = validateScopes });

        using (var scope = provider.CreateScope())
            scope.ServiceProvider.GetRequiredService<SavePipelineModel.Db>().Database.EnsureCreated();

        return new(provider, connection);
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

    public async ValueTask DisposeAsync()
    {
        await Provider.DisposeAsync();
        await connection.DisposeAsync();
    }
}

/// <summary>Writes one line for each row it is given: how it changed, what it holds, and what it held before.</summary>
public sealed class LoggingSaveHandler<TEntity>(SaveLog log) : ISaveHandler<TEntity> where TEntity : class
{
    public ValueTask HandleAsync(SavedRow<TEntity> row, CancellationToken cancellationToken)
    {
        var name = (row.Entity as SavePipelineModel.INamed)?.Name;
        log.Add($"{typeof(TEntity).Name} handler: {row.Change} {row.Entity.GetType().Name} '{name}'");
        return ValueTask.CompletedTask;
    }
}

/// <summary>Writes what a modified order held before the save next to what it holds now.</summary>
public sealed class OrderBeforeAndAfter(SaveLog log) : ISaveHandler<SavePipelineModel.Order>
{
    public ValueTask HandleAsync(SavedRow<SavePipelineModel.Order> row, CancellationToken cancellationToken)
    {
        log.Add($"{row.Change}: '{row.Original?.Name}'/'{row.Original?.Status}' -> '{row.Entity.Name}'/'{row.Entity.Status}'");
        return ValueTask.CompletedTask;
    }
}

/// <summary>Writes each report it is given, as any other observer of the context.</summary>
public sealed class LoggingSaveObserver(SaveLog log) : IShiftDbContextSaveObserver
{
    public ValueTask SavedAsync(ShiftDbContext context, SaveReport report, CancellationToken cancellationToken)
    {
        log.Add("observer: " + string.Join(", ", report.Rows.Select(row => $"{row.Change} {row.EntityType.Name}")));
        return ValueTask.CompletedTask;
    }
}
