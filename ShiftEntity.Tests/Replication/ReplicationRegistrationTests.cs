using EntityFrameworkCore.Triggered;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.Model.Replication;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Replication;

/// <summary>
/// Pins what the two replication registrations do to the host's DbContext: they switch the triggers pipeline on (the
/// after-save trigger needs it, and so does the catch-up's SaveChangesWithoutTriggersAsync) and nothing else. The host
/// registers the context itself, with its own provider and lifetimes, in any order and in any form (AddDbContext, a
/// pool, a factory). The registrations used to call AddDbContext a second time: that took over the context's lifetime
/// when it came first, and gave a pooled factory a scoped configuration its singleton options cannot use.
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

    public sealed class SaveRecorder
    {
        public int Saves;
    }

    public sealed class RecordingTrigger(SaveRecorder recorder) : IAfterSaveTrigger<RegisteredItem>
    {
        public Task AfterSave(ITriggerContext<RegisteredItem> context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref recorder.Saves);
            return Task.CompletedTask;
        }
    }

    private readonly SqliteConnection connection = new("DataSource=:memory:");
    private readonly SaveRecorder recorder = new();

    public ReplicationRegistrationTests() => connection.Open();

    public void Dispose() => connection.Dispose();

    private ServiceCollection HostServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(recorder);
        services.AddTransient<IAfterSaveTrigger<RegisteredItem>, RecordingTrigger>();
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
    public async Task Triggers_fire_on_the_hosts_context_with_either_registration(bool afterSaveSide)
    {
        var services = HostServices();
        services.AddDbContext<RegisteredDbContext>(o => o.UseSqlite(connection));
        if (afterSaveSide)
            services.AddShiftEntityCosmosDbReplicationTrigger<RegisteredDbContext>(_ => { });
        else
            services.AddShiftEntityCosmosDbReplication<RegisteredDbContext>();

        await using var provider = services.BuildServiceProvider(validateScopes: true);
        await using var scope = provider.CreateAsyncScope();
        await SaveOneItemAsync(scope.ServiceProvider.GetRequiredService<RegisteredDbContext>());

        Assert.Equal(1, recorder.Saves);
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
    public async Task A_pooled_factory_gets_the_triggers_too()
    {
        var services = HostServices();
        services.AddPooledDbContextFactory<RegisteredDbContext>(o => o.UseSqlite(connection));
        services.AddShiftEntityCosmosDbReplication<RegisteredDbContext>();

        await using var provider = services.BuildServiceProvider(validateScopes: true);
        var factory = provider.GetRequiredService<IDbContextFactory<RegisteredDbContext>>();
        await using var db = await factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        await SaveOneItemAsync(db);

        Assert.Equal(1, recorder.Saves);
    }
}
