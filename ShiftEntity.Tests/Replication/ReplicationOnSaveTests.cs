using System.Collections.Concurrent;
using System.Net;
using Microsoft.Azure.Cosmos;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.CosmosDbReplication;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.Model.Replication;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Replication;

/// <summary>
/// Pins what replication on save leaves on the caller's side. The sync that a save starts replicates in a background
/// task and then records itself, while the entity it holds is the caller's own instance, still tracked by the caller's
/// context. A unit of work that saves more than once on one context (a seeder, for example) must not see that record as
/// a change of its own. The sync used to mark the instance as replicated, so the caller's next save wrote the row
/// again, added a temporal history row and replicated the row a second time. The sync still has to remember what it
/// wrote: when a later save of the same instance moves the document or deletes the row, the document the earlier sync
/// wrote is removed. Runs the real registration on SQLite, against a Cosmos DB stand-in that records what it is asked
/// to do.
/// </summary>
public class ReplicationOnSaveTests : IAsyncDisposable
{
    public sealed class ReplicatedItem : ShiftEntity<ReplicatedItem>, IShiftEntityReplication
    {
        public string Code { get; set; } = "";
        public DateTimeOffset? LastReplicationDate { get; set; }
        public string? LastReplicationStamp { get; set; }
    }

    // Not replicated: the unrelated row of a later save.
    public sealed class PlainNote : ShiftEntity<PlainNote>
    {
        public string Text { get; set; } = "";
    }

    public sealed class OnSaveDbContext(DbContextOptions<OnSaveDbContext> options) : ShiftDbContext(options)
    {
        public DbSet<ReplicatedItem> Items => Set<ReplicatedItem>();
        public DbSet<PlainNote> Notes => Set<PlainNote>();
    }

    // The document id comes from the item's code, so a new code moves the document.
    public sealed class ItemDocument
    {
        public string id { get; set; } = "";
    }

    //Reads the sync's own messages. "Starting" is logged during the save; "Succeeded" or "Failed" when the
    //background task has finished, the bookkeeping write included.
    private sealed class SyncLog : ILoggerProvider, ILogger
    {
        private readonly ConcurrentQueue<long> started = new();
        private readonly ConcurrentQueue<string> failed = new();
        private readonly SemaphoreSlim finished = new(0);
        private int waitedFor;

        public IReadOnlyCollection<long> Started => started;
        public IReadOnlyCollection<string> Failed => failed;

        //Waits until every sync started so far has finished. It waits for the sync's message, not for a set time.
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
            if (state is not IReadOnlyList<KeyValuePair<string, object?>> values ||
                values.LastOrDefault().Value is not string format)
                return;

            if (format.StartsWith("CosmosDB Syncing is starting"))
                started.Enqueue(Convert.ToInt64(values.First(x => x.Key == "entityID").Value));
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

    private const string Database = "on-save";
    private const string Container = "items";
    private readonly SqliteConnection connection = new("DataSource=:memory:");
    private readonly SyncLog log = new();
    private readonly ConcurrentQueue<string> upserted = new();
    private readonly ConcurrentQueue<string> deleted = new();
    private readonly ServiceProvider provider;

    public ReplicationOnSaveTests()
    {
        connection.Open();
        var services = new ServiceCollection();
        services.AddLogging(x => x.AddProvider(log));
        services.AddDbContext<OnSaveDbContext>(o => o.UseSqlite(connection));
        services.AddShiftEntityCosmosDbReplicationOnSave<OnSaveDbContext>(o => o
            .SetUpReplication<OnSaveDbContext, ReplicatedItem>(Cosmos(), Database)
            .Replicate<ItemDocument>(Container, x => x.id, x => new ItemDocument { id = x.Entity.Code }));
        provider = services.BuildServiceProvider(validateScopes: true);
    }

    public async ValueTask DisposeAsync()
    {
        await log.AllFinishedAsync();   // no sync may still use the connection
        await provider.DisposeAsync();
        await connection.DisposeAsync();
    }

    //Accepts every write and records the document ids it was asked to write and to delete.
    private CosmosClient Cosmos()
    {
        var properties = Substitute.For<ContainerResponse>();
        properties.Resource.Returns(new ContainerProperties(Container, "/id"));

        var container = Substitute.For<Microsoft.Azure.Cosmos.Container>();
        container.ReadContainerAsync(Arg.Any<ContainerRequestOptions>(), Arg.Any<CancellationToken>()).Returns(properties);
        container.UpsertItemAsync(Arg.Any<ItemDocument>(), Arg.Any<PartitionKey?>(), Arg.Any<ItemRequestOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                upserted.Enqueue(call.Arg<ItemDocument>()!.id);
                return Task.FromResult(Response(HttpStatusCode.OK));
            });
        container.DeleteItemAsync<ItemDocument>(Arg.Any<string>(), Arg.Any<PartitionKey>(),
                Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                deleted.Enqueue(call.Arg<string>()!);
                return Task.FromResult(Response(HttpStatusCode.NoContent));
            });

        var database = Substitute.For<Microsoft.Azure.Cosmos.Database>();
        database.GetContainer(Container).Returns(container);

        var client = Substitute.For<CosmosClient>();
        client.ClientOptions.Returns(new CosmosClientOptions());
        client.GetDatabase(Database).Returns(database);
        return client;
    }

    private static ItemResponse<ItemDocument> Response(HttpStatusCode status)
    {
        var response = Substitute.For<ItemResponse<ItemDocument>>();
        response.StatusCode.Returns(status);
        return response;
    }

    //The caller: the context of a scope of its own, as a request or a seeder has.
    private static async Task<OnSaveDbContext> CallerAsync(AsyncServiceScope scope)
    {
        var db = scope.ServiceProvider.GetRequiredService<OnSaveDbContext>();
        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        return db;
    }

    //Adds an item and saves it, then waits until its sync has written the bookkeeping.
    private async Task<ReplicatedItem> AddSyncedItemAsync(OnSaveDbContext caller)
    {
        var item = new ReplicatedItem { Code = "item-1" };
        caller.Items.Add(item);
        await caller.SaveChangesAsync(TestContext.Current.CancellationToken);
        await log.AllFinishedAsync();
        return item;
    }

    private async Task<ReplicatedItem> StoredAsync(long id)
    {
        using var check = new OnSaveDbContext(
            new DbContextOptionsBuilder<OnSaveDbContext>().UseSqlite(connection).Options);
        return await check.Items.AsNoTracking().SingleAsync(x => x.ID == id, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_later_save_on_the_same_context_does_not_write_the_synced_row_again()
    {
        await using var scope = provider.CreateAsyncScope();
        var caller = await CallerAsync(scope);
        var item = await AddSyncedItemAsync(caller);

        //The sync recorded the replicated version's save date and the document it wrote.
        var stored = await StoredAsync(item.ID);
        Assert.Equal((DateTimeOffset?)item.LastSaveDate, stored.LastReplicationDate);
        Assert.Equal("item-1", LastReplicationStamp.Deserialize(stored.LastReplicationStamp)?.Id);

        caller.Notes.Add(new PlainNote { Text = "Unrelated" });
        var written = await caller.SaveChangesAsync(TestContext.Current.CancellationToken);
        await log.AllFinishedAsync();

        //The second save wrote the note only, and the item was synced once.
        Assert.Equal((1, 1, 1), (written, log.Started.Count, upserted.Count));
        Assert.Equal((EntityState.Unchanged, (DateTimeOffset?)null, (string?)null),
            (caller.Entry(item).State, item.LastReplicationDate, item.LastReplicationStamp));
        Assert.Empty(log.Failed);
    }

    [Fact]
    public async Task A_later_save_that_moves_the_document_removes_the_one_the_first_sync_wrote()
    {
        await using var scope = provider.CreateAsyncScope();
        var caller = await CallerAsync(scope);
        var item = await AddSyncedItemAsync(caller);

        item.Code = "item-2";
        await caller.SaveChangesAsync(TestContext.Current.CancellationToken);
        await log.AllFinishedAsync();

        Assert.Equal(["item-1"], deleted);
        Assert.Equal(["item-1", "item-2"], upserted);
        Assert.Equal([item.ID, item.ID], log.Started);    // one sync per save
        Assert.Equal("item-2", LastReplicationStamp.Deserialize((await StoredAsync(item.ID)).LastReplicationStamp)?.Id);
        Assert.Empty(log.Failed);
    }

    [Fact]
    public async Task A_row_deleted_after_its_sync_on_the_same_context_has_its_document_removed()
    {
        await using var scope = provider.CreateAsyncScope();
        var caller = await CallerAsync(scope);
        var item = await AddSyncedItemAsync(caller);

        caller.Items.Remove(item);                          // a hard delete
        await caller.SaveChangesAsync(TestContext.Current.CancellationToken);
        await log.AllFinishedAsync();

        Assert.Equal(["item-1"], deleted);
        Assert.Empty(log.Failed);
    }
}
