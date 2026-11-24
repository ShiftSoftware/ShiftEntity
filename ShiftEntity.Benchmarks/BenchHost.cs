using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Attention;
using ShiftSoftware.ShiftEntity.EFCore;

namespace ShiftSoftware.ShiftEntity.Benchmarks;

// ── The model ───────────────────────────────────────────────────────────────────

/// <summary>The row the benchmarks save: a name and twenty text columns.</summary>
public class Plain : ShiftEntity<Plain>
{
    public string? Name { get; set; }
    public string? P01 { get; set; } public string? P02 { get; set; } public string? P03 { get; set; } public string? P04 { get; set; } public string? P05 { get; set; }
    public string? P06 { get; set; } public string? P07 { get; set; } public string? P08 { get; set; } public string? P09 { get; set; } public string? P10 { get; set; }
    public string? P11 { get; set; } public string? P12 { get; set; } public string? P13 { get; set; } public string? P14 { get; set; } public string? P15 { get; set; }
    public string? P16 { get; set; } public string? P17 { get; set; } public string? P18 { get; set; } public string? P19 { get; set; } public string? P20 { get; set; }

    public static Plain New() => new()
    {
        Name = "Row",
        P01 = "value P01", P02 = "value P02", P03 = "value P03", P04 = "value P04", P05 = "value P05",
        P06 = "value P06", P07 = "value P07", P08 = "value P08", P09 = "value P09", P10 = "value P10",
        P11 = "value P11", P12 = "value P12", P13 = "value P13", P14 = "value P14", P15 = "value P15",
        P16 = "value P16", P17 = "value P17", P18 = "value P18", P19 = "value P19", P20 = "value P20",
    };
}

/// <summary>
/// A second type, which no benchmark saves. It takes part in attention, as most hosts have a type that does, and the
/// "handlers for other types" are its handlers.
/// </summary>
public class Gadget : ShiftEntity<Gadget>, IHasAttention, IHasAttentionEvaluator<Gadget>
{
    public string Name { get; set; } = "";
    public bool HasActiveAttention { get; set; }
    public AttentionSeverity? HighestSeverity { get; set; }
    public int ActiveSignalCount { get; set; }

    public AttentionSignal? EvaluateAttention(AttentionContext<Gadget> context) => null;
}

/// <summary>The framework's context.</summary>
public class FrameworkDb(DbContextOptions<FrameworkDb> options) : ShiftDbContext(options)
{
    public DbSet<Plain> Plains => Set<Plain>();
    public DbSet<Gadget> Gadgets => Set<Gadget>();
}

/// <summary>The same rows on a context that is EF's alone: what a save costs before the framework adds anything.</summary>
public class EfDb(DbContextOptions<EfDb> options) : DbContext(options)
{
    public DbSet<Plain> Plains => Set<Plain>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<Plain>().Ignore(x => x.ReloadAfterSave).ToTable("Plains");
}

// ── Handlers that do nothing ────────────────────────────────────────────────────

#if !BEFORE
public sealed class NothingBeforeSave<TEntity> : IBeforeSaveHandler<TEntity> where TEntity : class
{
    public ValueTask BeforeSaveAsync(PendingRows<TEntity> rows, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

public sealed class NothingAfterSave<TEntity> : IAfterSaveHandler<TEntity> where TEntity : class
{
    public ValueTask AfterSaveAsync(SavedRows<TEntity> rows, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

public sealed class NothingAfterCommit<TEntity> : IAfterCommitHandler<TEntity> where TEntity : class
{
    public ValueTask AfterCommitAsync(SavedRows<TEntity> rows, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

[NeedsOriginal]
public sealed class NothingAfterCommitWithOriginal<TEntity> : IAfterCommitHandler<TEntity> where TEntity : class
{
    public ValueTask AfterCommitAsync(SavedRows<TEntity> rows, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
#endif

// ── The hosts ───────────────────────────────────────────────────────────────────

/// <summary>The hosts a benchmark runs in. The letters are the scenarios of the budget.</summary>
public static class Hosts
{
    public const string EfAlone = "EF alone";
    public const string NoHandlers = "A: no handlers";
    public const string HandlersForOtherTypes = "B: handlers for other types";
    public const string AfterCommit = "C: after-commit handler";
    public const string BeforeSave = "D: before-save handler";
    public const string AfterCommitWithOriginal = "E: after-commit handler with old values";
    public const string AfterSave = "F, G: after-save handler";

#if BEFORE
    public static IEnumerable<string> ForLargeSaves => [EfAlone, NoHandlers];
    public static IEnumerable<string> ForSmallSaves => [EfAlone, NoHandlers];
#else
    public static IEnumerable<string> ForLargeSaves => [EfAlone, NoHandlers, HandlersForOtherTypes, AfterCommit, BeforeSave, AfterCommitWithOriginal];
    public static IEnumerable<string> ForSmallSaves => [EfAlone, NoHandlers, HandlersForOtherTypes, AfterCommit, BeforeSave, AfterSave];
#endif

    public const string Sqlite = "SQLite";
    public const string SqlServer = "SQL Server";

    /// <summary>SQLite in memory, or the SQL Server that SHIFT_ENTITY_BENCH_SQL names.</summary>
    public static IEnumerable<string> Providers =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SHIFT_ENTITY_BENCH_SQL")) ? [Sqlite] : [SqlServer];
}

/// <summary>One host on a database of its own, which goes with it.</summary>
public sealed class BenchHost : IDisposable
{
    private readonly SqliteConnection? sqlite;
    private readonly string? catalog;
    private readonly string? server;

    public ServiceProvider Services { get; }
    public bool IsEfAlone { get; }

    public BenchHost(string host, string provider)
    {
        IsEfAlone = host == Hosts.EfAlone;

        Action<DbContextOptionsBuilder> database;

        if (provider == Hosts.SqlServer)
        {
            var builder = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SHIFT_ENTITY_BENCH_SQL"));
            server = builder.ConnectionString;
            catalog = "ShiftEntityBench_" + Guid.NewGuid().ToString("N");
            builder.InitialCatalog = catalog;

            var connect = builder.ConnectionString;
            database = options => options.UseSqlServer(connect);
        }
        else
        {
            // One connection, kept open: the database lives as long as it does.
            sqlite = new SqliteConnection("Data Source=:memory:");
            sqlite.Open();
            database = options => options.UseSqlite(sqlite);
        }

        var services = new ServiceCollection();
        services.AddLogging();

        if (IsEfAlone)
            services.AddDbContext<EfDb>(database);
        else
            services.AddDbContext<FrameworkDb>(database);

#if !BEFORE
        switch (host)
        {
            case Hosts.HandlersForOtherTypes:
                services.AddSaveHandler<NothingBeforeSave<Gadget>>().AddSaveHandler<NothingAfterSave<Gadget>>().AddSaveHandler<NothingAfterCommitWithOriginal<Gadget>>();
                break;
            case Hosts.AfterCommit:
                services.AddSaveHandler<NothingAfterCommit<Plain>>();
                break;
            case Hosts.BeforeSave:
                services.AddSaveHandler<NothingBeforeSave<Plain>>();
                break;
            case Hosts.AfterCommitWithOriginal:
                services.AddSaveHandler<NothingAfterCommitWithOriginal<Plain>>();
                break;
            case Hosts.AfterSave:
                services.AddSaveHandler<NothingAfterSave<Plain>>();
                break;
        }
#endif

        Services = services.BuildServiceProvider();

        using var scope = Services.CreateScope();
        Context(scope).Database.EnsureCreated();
    }

    /// <summary>The host's context in a scope: the framework's, or EF's own.</summary>
    public DbContext Context(IServiceScope scope) =>
        IsEfAlone ? scope.ServiceProvider.GetRequiredService<EfDb>() : scope.ServiceProvider.GetRequiredService<FrameworkDb>();

    public void Dispose()
    {
        Services.Dispose();
        sqlite?.Dispose();

        if (catalog is null)
            return;

        SqlConnection.ClearAllPools();

        using var connection = new SqlConnection(server);
        connection.Open();

        using var drop = connection.CreateCommand();
        drop.CommandText = $"IF DB_ID(N'{catalog}') IS NOT NULL BEGIN ALTER DATABASE [{catalog}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{catalog}]; END";
        drop.ExecuteNonQuery();
    }
}
