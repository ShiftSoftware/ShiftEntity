using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.CosmosDbReplication.Exceptions;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.Model.Replication;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Replication;

/// <summary>
/// Pins what the two replication registrations do to the host's DbContext: nothing. They register the framework's own
/// services and never the context, which the host registers itself, with its own provider and lifetimes, in any order
/// and in any form (AddDbContext, a pool, a factory). They used to switch the trigger library on for it, because
/// replication ran as a trigger. They no longer do, and both add the check that stops a host that still has triggers.
/// And the check of the registrations themselves when the host starts.
/// </summary>
public class ReplicationRegistrationTests : IDisposable
{
    public sealed class RegisteredItem : ShiftEntity<RegisteredItem>, IShiftEntityReplication
    {
        public string Name { get; set; } = "";
        public DateTimeOffset? LastReplicationDate { get; set; }
        public string? LastReplicationStamp { get; set; }
    }

    public sealed class RegisteredDbContext(DbContextOptions<RegisteredDbContext> options) : ShiftDbContext(options)
    {
        public DbSet<RegisteredItem> Items => Set<RegisteredItem>();
    }

    private readonly SqliteConnection connection = new("DataSource=:memory:");

    public ReplicationRegistrationTests() => connection.Open();

    public void Dispose()
    {
        cosmos.Dispose();
        connection.Dispose();
    }

    private static ServiceCollection HostServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        return services;
    }

    private static async Task SaveOneItemAsync(RegisteredDbContext db)
    {
        var ct = TestContext.Current.CancellationToken;
        await db.Database.EnsureCreatedAsync(ct);
        db.Items.Add(new RegisteredItem { Name = "Item" });
        await db.SaveChangesAsync(ct);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Neither_registration_switches_the_trigger_library_on_and_both_add_the_check_for_it(bool afterSaveSide)
    {
        var services = HostServices();
        services.AddDbContext<RegisteredDbContext>(o => o.UseSqlite(connection));
        if (afterSaveSide)
            services.AddShiftEntityCosmosDbReplicationOnSave<RegisteredDbContext>(_ => { });
        else
            services.AddShiftEntityCosmosDbReplication<RegisteredDbContext>();

        // The check that stops a host with triggers, added once.
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IHostedService) && descriptor.ImplementationType == typeof(TriggerLibraryCheck));

        await using var provider = services.BuildServiceProvider(validateScopes: true);
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RegisteredDbContext>();

        // The context's options are the host's own, plus the framework's extension that every ShiftDbContext carries.
        var extensions = db.GetService<IDbContextOptions>().Extensions.Select(extension => extension.GetType().Assembly.GetName().Name).ToList();
        Assert.DoesNotContain(extensions, assembly => assembly!.StartsWith("EntityFrameworkCore.Triggered", StringComparison.Ordinal));

        await SaveOneItemAsync(db);
        Assert.Equal(1, await db.Items.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void The_hosts_registration_decides_the_context_lifetime_even_when_it_comes_second()
    {
        var services = HostServices();
        services.AddShiftEntityCosmosDbReplication<RegisteredDbContext>();
        services.AddDbContext<RegisteredDbContext>(o => o.UseSqlite(connection), ServiceLifetime.Transient);

        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        Assert.NotSame(scope.ServiceProvider.GetRequiredService<RegisteredDbContext>(),
            scope.ServiceProvider.GetRequiredService<RegisteredDbContext>());
    }

    [Fact]
    public async Task A_pooled_factory_is_left_as_the_host_registered_it()
    {
        var services = HostServices();
        services.AddPooledDbContextFactory<RegisteredDbContext>(o => o.UseSqlite(connection));
        services.AddShiftEntityCosmosDbReplication<RegisteredDbContext>();

        await using var provider = services.BuildServiceProvider(validateScopes: true);
        var factory = provider.GetRequiredService<IDbContextFactory<RegisteredDbContext>>();
        await using var db = await factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        await SaveOneItemAsync(db);

        Assert.Equal(1, await db.Items.CountAsync(TestContext.Current.CancellationToken));
    }

    public sealed class RegisteredDocument
    {
        public string id { get; set; } = "";
    }

    private readonly RecordingCosmos cosmos = new RecordingCosmos(ReplicationPinHost.Database).Container("items", "/id");

    // The warnings the host logs, with their exceptions.
    public sealed class WarningLog : ILoggerProvider, ILogger
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<(string Message, Exception? Exception)> warnings = new();

        public IReadOnlyList<(string Message, Exception? Exception)> Warnings => warnings.ToList();

        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                warnings.Enqueue((formatter(state, exception), exception));
        }

        public void Dispose() { }
    }

    private readonly WarningLog log = new();

    public enum Registration { Valid, Refused, Failing }

    // The item's registration: a valid one, one with a combination the framework refuses, or one whose builder fails for
    // another reason, as one that reads a setting its environment lacks.
    private void AddReplication(IServiceCollection services, Registration registration)
    {
        services.AddLogging(x => x.AddProvider(log));
        services.AddDbContext<RegisteredDbContext>(o => o.UseSqlite(connection));
        services.AddShiftEntityCosmosDbReplicationOnSave<RegisteredDbContext>(o =>
        {
            if (registration == Registration.Failing)
                throw new InvalidOperationException("No Cosmos DB connection is configured in this environment.");

            var items = o.SetUpReplication<RegisteredDbContext, RegisteredItem>(cosmos.Client, ReplicationPinHost.Database);
            items.Replicate<RegisteredDocument>("items", x => x.id, x => new RegisteredDocument { id = x.Entity.ID.ToString() });
            if (registration == Registration.Refused)
                items.ReplicateTree<RegisteredDocument>("items", x => x.id);
        });
    }

    private IHost BuildHost(bool web, Registration registration)
    {
        if (web)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            AddReplication(builder.Services, registration);
            return builder.Build();
        }

        // A generic host, as the isolated Azure Functions worker is.
        var generic = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        AddReplication(generic.Services, registration);
        return generic.Build();
    }

    // The registrations refuse some combinations, such as Replicate with ReplicateTree. They are built in a scope at
    // the first replicated save, after that save has committed, and they then failed every replicated save of the
    // process. They are built once when the host starts too, so such a host does not start.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_host_with_a_refused_registration_fails_to_start(bool web)
    {
        using var host = BuildHost(web, Registration.Refused);

        var refusal = await Assert.ThrowsAsync<ReplicationRegistrationException>(() => host.StartAsync(TestContext.Current.CancellationToken));
        Assert.Contains("registers both Replicate and ReplicateTree", refusal.Message);
    }

    // A builder that fails for another reason, such as a setting a development or test environment lacks, let its host
    // start and failed at the first replicated save. It still does, and the start logs why it could not be built. The
    // row of that save is committed by then, so the failure reaches the caller as a step after the commit that failed.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_host_whose_registrations_fail_for_another_reason_starts_and_logs_why(bool web)
    {
        using var host = BuildHost(web, Registration.Failing);

        await host.StartAsync(TestContext.Current.CancellationToken);
        var warning = Assert.Single(log.Warnings, x => x.Message.Contains("replication registrations could not be built"));
        Assert.Contains("built again at the first replicated save", warning.Message);
        Assert.Equal("No Cosmos DB connection is configured in this environment.", warning.Exception?.Message);

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var afterCommit = await Assert.ThrowsAsync<AfterCommitException>(() =>
                SaveOneItemAsync(scope.ServiceProvider.GetRequiredService<RegisteredDbContext>()));
            var failure = Assert.IsType<InvalidOperationException>(Assert.Single(afterCommit.Failures));
            Assert.Equal("No Cosmos DB connection is configured in this environment.", failure.Message);
        }
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_host_with_valid_registrations_starts(bool web)
    {
        using var host = BuildHost(web, Registration.Valid);

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
        Assert.Empty(log.Warnings);
    }

    // Without a host, the refusal still comes, at the first replicated save, once its row is committed.
    [Fact]
    public async Task Without_a_host_a_refused_registration_fails_the_first_replicated_save()
    {
        var services = new ServiceCollection();
        AddReplication(services, Registration.Refused);
        await using var provider = services.BuildServiceProvider(validateScopes: true);

        await using var scope = provider.CreateAsyncScope();
        var afterCommit = await Assert.ThrowsAsync<AfterCommitException>(() =>
            SaveOneItemAsync(scope.ServiceProvider.GetRequiredService<RegisteredDbContext>()));
        var refusal = Assert.IsType<ReplicationRegistrationException>(Assert.Single(afterCommit.Failures));
        Assert.Contains("registers both Replicate and ReplicateTree", refusal.Message);
    }
}
