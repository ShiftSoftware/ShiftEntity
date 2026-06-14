using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Attention;
using ShiftSoftware.ShiftEntity.Tests.DataLevelAccess.Scenario;

namespace ShiftSoftware.ShiftEntity.Tests.Attention.Scenario;

/// <summary>
/// Singleton recorder shared by the (scoped, fresh-instance-per-event) consumers. Tests
/// await <see cref="WaitUntilAsync"/> rather than sleeping; for "nothing was published"
/// assertions they publish a sentinel event and wait for it instead — the dispatcher drains
/// FIFO with a single reader, so once the sentinel has arrived, any earlier event would
/// have arrived too.
/// </summary>
public sealed class AttentionEventSink
{
    private readonly object gate = new();
    private readonly List<(string Consumer, AttentionRaised Event)> received = [];

    public void Record(string consumer, AttentionRaised attentionRaised)
    {
        lock (gate)
            received.Add((consumer, attentionRaised));
    }

    public List<(string Consumer, AttentionRaised Event)> Snapshot()
    {
        lock (gate)
            return [.. received];
    }

    /// <summary>
    /// Polls until <paramref name="condition"/> holds for the received-events snapshot, or
    /// the timeout (default 5s) elapses — in which case the last snapshot is returned and
    /// the caller's assertions produce the diagnostic failure.
    /// </summary>
    public async Task<List<(string Consumer, AttentionRaised Event)>> WaitUntilAsync(
        Func<List<(string Consumer, AttentionRaised Event)>, bool> condition,
        TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));

        while (true)
        {
            var snapshot = Snapshot();

            if (condition(snapshot) || DateTimeOffset.UtcNow > deadline)
                return snapshot;

            await Task.Delay(25);
        }
    }
}

/// <summary>Records every event (with its own type name) into the shared sink.</summary>
public sealed class RecordingAttentionConsumer : IAttentionConsumer
{
    private readonly AttentionEventSink sink;

    public RecordingAttentionConsumer(AttentionEventSink sink) => this.sink = sink;

    public Task HandleAsync(AttentionRaised attentionRaised, CancellationToken cancellationToken)
    {
        sink.Record(nameof(RecordingAttentionConsumer), attentionRaised);
        return Task.CompletedTask;
    }
}

/// <summary>A second recording consumer type, for "all consumers receive every event" assertions.</summary>
public sealed class SecondRecordingAttentionConsumer : IAttentionConsumer
{
    private readonly AttentionEventSink sink;

    public SecondRecordingAttentionConsumer(AttentionEventSink sink) => this.sink = sink;

    public Task HandleAsync(AttentionRaised attentionRaised, CancellationToken cancellationToken)
    {
        sink.Record(nameof(SecondRecordingAttentionConsumer), attentionRaised);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Always throws — registered <em>before</em> the recording consumers to prove a failing
/// consumer is logged and isolated rather than poisoning the event for the rest.
/// </summary>
public sealed class ThrowingAttentionConsumer : IAttentionConsumer
{
    public Task HandleAsync(AttentionRaised attentionRaised, CancellationToken cancellationToken)
        => throw new InvalidOperationException("Deliberate test-consumer failure.");
}

/// <summary>
/// DI host + running dispatcher for the emission tests, shaped like a real app:
/// EF InMemory <see cref="AttentionTestDbContext"/> (fresh database per host, transactions
/// ignored — the indexed-mode save path opens one), the scoped identity services the
/// repository resolves, the shared <see cref="AttentionEventSink"/>, and whatever the test
/// registers via <paramref name="configure"/> (consumers, evaluators — or nothing, for the
/// not-opted-in case). Hosted services are started on build and stopped on dispose, so the
/// channel drain loop runs exactly like it would under a real generic host. Scope
/// validation is on, the same startup check a Development host runs.
/// </summary>
public sealed class AttentionApp : IAsyncDisposable
{
    public ServiceProvider Provider { get; }
    public AttentionEventSink Sink { get; }

    private readonly List<IHostedService> startedServices;

    private AttentionApp(ServiceProvider provider, List<IHostedService> startedServices)
    {
        Provider = provider;
        Sink = provider.GetRequiredService<AttentionEventSink>();
        this.startedServices = startedServices;
    }

    public static async Task<AttentionApp> StartAsync(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();

        services.AddLogging();

        services.AddDbContext<AttentionTestDbContext>(options => options
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            // The indexed-mode save path wraps the save in a transaction; InMemory ignores
            // transactions, which is fine here — commit ordering is pinned by the
            // SQL-Server-backed sample-app suites.
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));

        // What AddShiftEntityWebSharedCore / AddShiftEntity contribute, minimally.
        // (IDefaultDataLevelAccess included: the repository constructor resolves it through
        // EF's throwing GetService<T>.)
        services.AddScoped<ICurrentUserProvider>(_ => FakeCurrentUserProvider.Anonymous());
        services.AddScoped<IdentityClaimProvider>();
        services.AddSingleton<IHashIdService>(new RecordingHashIdService());
        services.AddSingleton<IDefaultDataLevelAccess>(new RecordingDefaultDataLevelAccess());

        services.AddSingleton<AttentionEventSink>();

        configure?.Invoke(services);

        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        var hostedServices = provider.GetServices<IHostedService>().ToList();

        foreach (var hostedService in hostedServices)
            await hostedService.StartAsync(CancellationToken.None);

        return new AttentionApp(provider, hostedServices);
    }

    public IServiceScope CreateScope() => Provider.CreateScope();

    public async ValueTask DisposeAsync()
    {
        using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        foreach (var hostedService in startedServices)
            await hostedService.StopAsync(stopTimeout.Token);

        await Provider.DisposeAsync();
    }
}
