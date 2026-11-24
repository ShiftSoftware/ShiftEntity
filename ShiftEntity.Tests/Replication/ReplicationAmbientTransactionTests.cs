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
/// Pins what an ambient transaction (<c>TransactionScope</c>) means for replication. The context is not told when a
/// scope commits, so it cannot start a sync at the right time there: a save of rows it replicates is refused inside
/// one, before it writes. With replication switched off the save joins the scope like any other. SQLite takes no part
/// in ambient transactions, so these run on SQL Server and are skipped without one (see <see cref="OwnedSqlCatalog"/>).
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
            services.AddShiftEntityCosmosDbReplicationOnSave<Db>(o => o.SetUpReplication<Db, Item>(Cosmos.Client, ReplicationPinHost.Database)
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

        public async Task<(int Items, int Notes)> StoredAsync()
        {
            await using var db = Bare();
            return (await db.Items.CountAsync(Ct), await db.Notes.CountAsync(Ct));
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

    // In a TransactionScope the context has no transaction of its own to follow. Its syncs used to wait for the
    // scope's completion event, which can run on another thread and has nothing before the commit. Now the save is
    // refused while nothing is written.
    [Fact]
    public async Task A_save_of_rows_it_replicates_is_refused_inside_a_TransactionScope_before_it_writes()
    {
        await using var host = await Host.CreateAsync("AmbientRefused");

        using (var ambient = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            await using var scope = host.Provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<Db>();
            var item = new Item { Code = "a", Region = "north" };
            db.Items.Add(item);

            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));

            Assert.Contains("cannot save Item rows, for which replication waits for the commit", refused.Message);
            Assert.Contains("an ambient transaction (TransactionScope)", refused.Message);
            Assert.Contains("Begin the transaction through this context's Database", refused.Message);
            Assert.Contains("SuppressReplication()", refused.Message);
            Assert.Equal(EntityState.Added, db.Entry(item).State);
            Assert.Equal(0, item.ID);

            ambient.Complete();
        }
        await host.Log.AllFinishedAsync();

        Assert.Equal((0, 0), await host.StoredAsync());
        Assert.Empty(host.Log.Started);
        Assert.Empty(host.Cosmos.Writes);
    }

    // A synchronous save in a TransactionScope without async flow is refused the same way.
    [Fact]
    public async Task A_synchronous_save_of_rows_it_replicates_is_refused_inside_a_TransactionScope()
    {
        await using var host = await Host.CreateAsync("AmbientSync");

        using (var ambient = new TransactionScope())
        {
            using var scope = host.Provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Db>();
            db.Items.Add(new Item { Code = "first", Region = "north" });

            Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
            ambient.Complete();
        }
        await host.Log.AllFinishedAsync();

        Assert.Equal((0, 0), await host.StoredAsync());
        Assert.Empty(host.Log.Started);
    }

    // A row with nothing that waits for the commit is saved in the scope, and commits or rolls back with it. So is a
    // replicated row once replication is switched off for the save: the scope holds it, and a catch-up replicates it.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Rows_with_nothing_that_waits_for_the_commit_are_saved_inside_a_TransactionScope(bool complete)
    {
        await using var host = await Host.CreateAsync("AmbientSaved");

        using (var ambient = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            await using var scope = host.Provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<Db>();

            db.Notes.Add(new Note { Text = "not replicated" });
            await db.SaveChangesAsync(Ct);

            db.Items.Add(new Item { Code = "a", Region = "north" });
            using (db.SuppressReplication())
                await db.SaveChangesAsync(Ct);

            if (complete)
                ambient.Complete();
        }
        await host.Log.AllFinishedAsync();

        Assert.Equal(complete ? (1, 1) : (0, 0), await host.StoredAsync());
        Assert.Empty(host.Log.Started);
        Assert.Empty(host.Cosmos.Writes);
    }

    // Work queued in code inside a TransactionScope used to run when the scope completed. The call is refused now:
    // the context cannot say when that is.
    [Fact]
    public async Task Work_cannot_be_queued_for_after_the_commit_inside_a_TransactionScope()
    {
        await using var host = await Host.CreateAsync("AmbientWork");
        var ran = new List<string>();

        foreach (var complete in new[] { false, true })
        {
            using var ambient = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);
            await using var scope = host.Provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<Db>();

            var refused = Assert.Throws<InvalidOperationException>(() => db.AfterCommit(() => ran.Add("ran")));
            Assert.Contains("cannot hold work for after the commit inside an ambient transaction (TransactionScope)", refused.Message);

            if (complete)
                ambient.Complete();
        }

        Assert.Empty(ran);
    }
}
