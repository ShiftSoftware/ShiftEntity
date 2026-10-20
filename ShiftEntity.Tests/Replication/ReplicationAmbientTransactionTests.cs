using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using static ShiftSoftware.ShiftEntity.Tests.Replication.PinModel;

namespace ShiftSoftware.ShiftEntity.Tests.Replication;

/// <summary>
/// Pins the saves made in an ambient transaction (<c>TransactionScope</c>): their syncs start once it commits, and one
/// row's syncs are combined there, as in an explicit transaction. SQLite takes no part in ambient transactions, so these
/// run on SQL Server and are skipped without one (see <see cref="OwnedSqlCatalog"/>).
/// </summary>
public class ReplicationAmbientTransactionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Host : IAsyncDisposable
    {
        private readonly OwnedSqlCatalog catalog;

        public RecordingCosmos Cosmos { get; } = new RecordingCosmos(ReplicationPinHost.Database).Container("items", "/id");
        public ReplicationSyncLog Log { get; } = new();
        public ServiceProvider Provider { get; }

        private Host(OwnedSqlCatalog catalog)
        {
            this.catalog = catalog;

            var services = new ServiceCollection();
            services.AddLogging(x => x.AddProvider(Log));
            services.AddDbContext<Db>(o => o.UseSqlServer(catalog.ConnectionString));
            services.AddShiftEntityCosmosDbReplicationTrigger<Db>(o => o.SetUpReplication<Db, Item>(Cosmos.Client, ReplicationPinHost.Database)
                .Replicate<ItemDocument>("items", x => x.id, x => Document(x.Entity)));
            Provider = services.BuildServiceProvider(validateScopes: true);
        }

        public static async Task<Host> CreateAsync(string name)
        {
            var host = new Host(await OwnedSqlCatalog.CreateAsync(name, Ct));
            await using var db = host.Bare();
            await db.GetService<IRelationalDatabaseCreator>().CreateTablesAsync(Ct);
            return host;
        }

        public Db Bare() => new(new DbContextOptionsBuilder<Db>().UseSqlServer(catalog.ConnectionString).Options);

        public async Task<Item> StoredAsync(long id)
        {
            await using var db = Bare();
            return await db.Items.AsNoTracking().SingleAsync(x => x.ID == id, Ct);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Log.AllFinishedAsync();
            }
            finally
            {
                await Provider.DisposeAsync();
                Cosmos.Dispose();
                await catalog.DisposeAsync();
            }
        }
    }

    // RV-09. In a TransactionScope the context has no transaction of its own, and its syncs used to be combined only in
    // an explicit one. Each save's syncs started at the commit on their own, so the add wrote a document for a row the
    // delete had removed, in 20 of 20 runs.
    [Fact]
    public async Task A_row_added_and_deleted_in_one_TransactionScope_writes_nothing()
    {
        await using var host = await Host.CreateAsync("AmbientAddDelete");

        using (var ambient = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            await using var scope = host.Provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<Db>();
            var item = new Item { Code = "a", Region = "north" };
            db.Items.Add(item);
            await db.SaveChangesAsync(Ct);
            db.Items.Remove(item);
            await db.SaveChangesAsync(Ct);
            ambient.Complete();
        }
        await host.Log.AllFinishedAsync();

        Assert.Empty(host.Log.Started);
        Assert.Empty(host.Cosmos.Writes);
    }

    // RV-09. The delete and the add of one key used to start at the commit as syncs of their own, so a slow delete
    // removed the new document after it was written.
    [Fact]
    public async Task A_row_deleted_and_added_again_in_one_TransactionScope_is_deleted_and_then_written_in_that_order()
    {
        await using var host = await Host.CreateAsync("AmbientDeleteAdd");
        await using (var scope = host.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Db>();
            db.Items.Add(new Item { Code = "a", Region = "north" });
            await db.SaveChangesAsync(Ct);
        }
        await host.Log.AllFinishedAsync();

        host.Cosmos.Refuse = write =>
        {
            if (write.Kind == "delete")
                Thread.Sleep(200);      // a slow delete
            return false;
        };

        using (var ambient = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            await using var scope = host.Provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<Db>();

            // One session for both saves, so that the second can give the new row the deleted row's key.
            await db.Database.OpenConnectionAsync(Ct);
            db.Items.Remove(await db.Items.SingleAsync(Ct));
            await db.SaveChangesAsync(Ct);
            var identityInsert = "SET IDENTITY_INSERT " +
                db.GetService<ISqlGenerationHelper>().DelimitIdentifier(db.Model.FindEntityType(typeof(Item))!.GetTableName()!) + " ON";
            await db.Database.ExecuteSqlRawAsync(identityInsert, Ct);
            db.Items.Add(new Item { ID = 1, Code = "b", Region = "north" });
            await db.SaveChangesAsync(Ct);

            Assert.Equal(["Item 1"], host.Log.Started);     // only the first save's, before the scope
            ambient.Complete();
        }
        await host.Log.AllFinishedAsync();

        Assert.Equal(["delete items/1", "upsert items/1"], host.Cosmos.Writes.Skip(1).Select(x => $"{x.Kind} {x.Container}/{x.Id}"));
        Assert.Contains("\"Code\":\"b\"", host.Cosmos.StoredLines("items").Single());
        var stored = await host.StoredAsync(1);
        Assert.Equal((DateTimeOffset?)stored.LastSaveDate, stored.LastReplicationDate);
    }

    // A synchronous save in a TransactionScope without async flow: its syncs wait for the commit too, and the saves of
    // the scope start theirs together.
    [Fact]
    public async Task Synchronous_saves_in_a_TransactionScope_replicate_once_it_commits()
    {
        await using var host = await Host.CreateAsync("AmbientSync");

        using (var ambient = new TransactionScope())
        {
            using var scope = host.Provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Db>();
            db.Items.Add(new Item { Code = "first", Region = "north" });
            db.SaveChanges();
            db.Items.Add(new Item { Code = "second", Region = "north" });
            db.SaveChanges();
            Assert.Empty(host.Log.Started);
            ambient.Complete();
        }
        await host.Log.AllFinishedAsync();

        Assert.Equal(["Item 1", "Item 2"], host.Log.Started.Order());
        Assert.Empty(host.Log.Failed);
        Assert.Equal(2, host.Cosmos.StoredLines("items").Count);
        foreach (var id in new[] { 1L, 2L })
        {
            var stored = await host.StoredAsync(id);
            Assert.Equal((DateTimeOffset?)stored.LastSaveDate, stored.LastReplicationDate);
        }
    }

    // The work queued in one ambient transaction runs once it commits, in the order it was queued, and none of it runs
    // when it does not commit.
    [Fact]
    public async Task Work_queued_in_a_TransactionScope_runs_in_order_once_it_commits_and_never_without_a_commit()
    {
        await using var host = await Host.CreateAsync("AmbientWork");
        var ran = new List<string>();

        foreach (var complete in new[] { false, true })
        {
            using var ambient = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);
            await using var scope = host.Provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<Db>();
            db.AfterCommit(() => ran.Add($"first, completed: {complete}"));
            db.AfterCommit(() => ran.Add($"second, completed: {complete}"));
            Assert.Empty(ran);
            if (complete)
                ambient.Complete();
        }

        Assert.Equal(["first, completed: True", "second, completed: True"], ran);
    }
}
