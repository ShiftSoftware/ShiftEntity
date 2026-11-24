using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ShiftSoftware.ShiftEntity.Benchmarks;

/// <summary>
/// A save of many rows: an insert and an update of 5,000, in each host. Every measurement is one save, prepared
/// outside the measurement: a new scope, a new context, and the rows added or loaded and changed.
/// </summary>
[Config(typeof(Config))]
public class LargeSaves
{
    private sealed class Config : ManualConfig
    {
        public Config()
        {
            // One save a measurement, in this process: a save of 5,000 rows is long enough to time on its own, and
            // the preparation of each must not be measured with it.
            AddJob(Job.Default
                .WithToolchain(InProcessEmitToolchain.Instance)
                .WithStrategy(RunStrategy.Monitoring)
                .WithWarmupCount(3)
                .WithIterationCount(12)
                .WithInvocationCount(1)
                .WithUnrollFactor(1));

            AddDiagnoser(MemoryDiagnoser.Default);
        }
    }

    [ParamsSource(nameof(HostNames))]
    public string Host { get; set; } = "";

    [ParamsSource(nameof(ProviderNames))]
    public string Provider { get; set; } = "";

    [Params(5000)]
    public int Rows { get; set; }

    public IEnumerable<string> HostNames => Hosts.ForLargeSaves;
    public IEnumerable<string> ProviderNames => Hosts.Providers;

    private BenchHost host = null!;
    private IServiceScope scope = null!;
    private DbContext db = null!;

    [GlobalSetup]
    public void Start()
    {
        host = new BenchHost(Host, Provider);

        // The rows the updates change. Saved a few times over, so that the code a save runs is compiled as it will
        // be for the measurements, also for the host that is measured first.
        for (var warmUp = 0; warmUp < 4; warmUp++)
        {
            using var seeding = host.Services.CreateScope();
            var context = host.Context(seeding);

            for (var i = 0; i < Rows; i++)
                context.Add(Plain.New());

            context.SaveChangesAsync().GetAwaiter().GetResult();

            foreach (var row in context.Set<Plain>().Local.ToList())
                row.P01 = "changed";

            context.SaveChangesAsync().GetAwaiter().GetResult();
        }
    }

    [GlobalCleanup]
    public void Stop() => host.Dispose();

    [IterationSetup(Target = nameof(Insert))]
    public void PrepareInsert()
    {
        scope = host.Services.CreateScope();
        db = host.Context(scope);

        for (var i = 0; i < Rows; i++)
            db.Add(Plain.New());
    }

    [IterationSetup(Target = nameof(Update))]
    public void PrepareUpdate()
    {
        scope = host.Services.CreateScope();
        db = host.Context(scope);

        var value = Guid.NewGuid().ToString("N");

        foreach (var row in db.Set<Plain>().OrderBy(x => x.ID).Take(Rows).ToList())
            row.P01 = value;
    }

    [IterationCleanup]
    public void EndOfSave() => scope.Dispose();

    [Benchmark]
    public Task<int> Insert() => db.SaveChangesAsync();

    [Benchmark]
    public Task<int> Update() => db.SaveChangesAsync();
}

/// <summary>
/// A save of one row, on its own and inside a transaction of the caller's, and a save with nothing to write. Each is
/// measured many times on one context, which is cleared after every save.
/// </summary>
[Config(typeof(Config))]
public class SmallSaves
{
    private sealed class Config : ManualConfig
    {
        public Config()
        {
            AddJob(Job.Default
                .WithToolchain(InProcessEmitToolchain.Instance)
                .WithWarmupCount(4)
                .WithIterationCount(12));

            AddDiagnoser(MemoryDiagnoser.Default);
        }
    }

    [ParamsSource(nameof(HostNames))]
    public string Host { get; set; } = "";

    [ParamsSource(nameof(ProviderNames))]
    public string Provider { get; set; } = "";

    public IEnumerable<string> HostNames => Hosts.ForSmallSaves;
    public IEnumerable<string> ProviderNames => Hosts.Providers;

    private BenchHost host = null!;
    private IServiceScope scope = null!;
    private DbContext db = null!;

    [GlobalSetup]
    public void Start()
    {
        host = new BenchHost(Host, Provider);
        scope = host.Services.CreateScope();
        db = host.Context(scope);
    }

    [GlobalCleanup]
    public void Stop()
    {
        scope.Dispose();
        host.Dispose();
    }

    /// <summary>One new row. With an after-save handler the context opens a transaction for it: scenario F.</summary>
    [Benchmark]
    public async Task<int> OneRow()
    {
        db.Add(Plain.New());
        var written = await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return written;
    }

    /// <summary>One new row inside a transaction the caller began: scenario G with an after-save handler.</summary>
    [Benchmark]
    public async Task<int> OneRowInATransaction()
    {
        await using var transaction = await db.Database.BeginTransactionAsync();

        db.Add(Plain.New());
        var written = await db.SaveChangesAsync();
        await transaction.CommitAsync();

        db.ChangeTracker.Clear();
        return written;
    }

    /// <summary>Nothing to write: scenario H.</summary>
    [Benchmark]
    public Task<int> Nothing() => db.SaveChangesAsync();
}
