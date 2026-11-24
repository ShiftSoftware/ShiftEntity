using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.CosmosDbReplication;
using ShiftSoftware.ShiftEntity.CosmosDbReplication.Services;
using ShiftSoftware.ShiftEntity.EFCore;
using Xunit;
using static ShiftSoftware.ShiftEntity.Tests.Replication.PinModel;

namespace ShiftSoftware.ShiftEntity.Tests.Replication;

/// <summary>
/// Pins how replication sits in the save: when a sync starts relative to an explicit transaction, which saves
/// replicate, what a host's save handlers do to the document (a before-save handler's change reaches it, and a row an
/// after-save handler saves again is written once), what a refused write leaves on the row, and that replication never
/// changes an audit column.
/// </summary>
/// <remarks>
/// Two pins were corrected when replication moved off the trigger library, each named in its comment: "the sync waits
/// for the commit" (a sync started before the commit of an explicit transaction, so a rolled-back save still reached
/// Cosmos DB; now it starts after the commit) and "every save replicates" (a save that went around the triggers skipped
/// replication; every save replicates now, and <c>SuppressReplication()</c> is the way to skip it).
/// </remarks>
public class ReplicationSavePipelinePinTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string NorthA =
        """upsert items/1 {"id":"1","Region":"north","ItemType":"Item","Rank":0,"Code":"a","CategoryID":null,"CategoryName":null,"Category":null}""";

    private static ReplicationPinHost ItemHost(Action<IServiceCollection>? services = null,
        Action<DbContextOptionsBuilder>? context = null) => new(
        (o, client) => o.SetUpReplication<Db, Item>(client, ReplicationPinHost.Database)
            .Replicate<ItemDocument>("items", x => x.id, x => Document(x.Entity)),
        services, context, ("items", ["/id"]));

    // The sync waits for the commit. Before the correction the sync started during SaveChanges, before the commit, and a
    // rollback did not stop it: the document of a row that did not exist was written.
    [Fact]
    public async Task Inside_an_explicit_transaction_nothing_starts_before_the_commit_and_a_rolled_back_save_writes_nothing()
    {
        await using var host = ItemHost();
        await using var scope = host.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Db>();

        await using (var transaction = await db.Database.BeginTransactionAsync(Ct))
        {
            db.Items.Add(new Item { Code = "a", Region = "north" });
            await db.SaveChangesAsync(Ct);

            Assert.Empty(host.Log.Started);     // nothing starts while the transaction's outcome is unknown

            await transaction.RollbackAsync(Ct);
        }

        await host.Log.AllFinishedAsync();

        Assert.False(await host.ExistsAsync<Item>(1));
        Assert.Empty(host.Log.Started);
        Assert.Empty(host.Cosmos.Writes);
    }

    // The sync waits for the commit. Before the correction the sync started during SaveChanges, before the commit.
    [Fact]
    public async Task Inside_an_explicit_transaction_that_commits_the_sync_starts_after_the_commit()
    {
        await using var host = ItemHost();
        await using var scope = host.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Db>();

        await using (var transaction = await db.Database.BeginTransactionAsync(Ct))
        {
            db.Items.Add(new Item { Code = "a", Region = "north" });
            await db.SaveChangesAsync(Ct);

            Assert.Empty(host.Log.Started);

            await transaction.CommitAsync(Ct);

            Assert.Equal(["Item 1"], host.Log.Started);
        }

        await host.Log.AllFinishedAsync();

        Assert.True(await host.ExistsAsync<Item>(1));
        Assert.Equal([NorthA], host.Cosmos.WriteLines);
        Assert.Equal((DateTimeOffset?)(await host.StoredAsync<Item>(1)).LastSaveDate, (await host.StoredAsync<Item>(1)).LastReplicationDate);
    }

    // Every save replicates. A save with every handler switched off still replicates: replication is a part of the
    // framework, not a handler. Before the correction a save that went around the triggers started no sync and left the
    // row dirty.
    [Fact]
    public async Task A_save_with_every_handler_switched_off_replicates_like_any_save()
    {
        await using var host = ItemHost();
        await using var scope = host.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Db>();

        db.Items.Add(new Item { Code = "a", Region = "north" });
        using (db.SuppressAllSaveHandlers())
            await db.SaveChangesAsync(Ct);
        await host.Log.AllFinishedAsync();

        Assert.Equal(["Item 1"], host.Log.Started);
        Assert.Equal([NorthA], host.Cosmos.WriteLines);
        var stored = await host.StoredAsync<Item>(1);
        Assert.Equal((DateTimeOffset?)stored.LastSaveDate, stored.LastReplicationDate);
    }

    // Every save replicates: what a save that must not replicate uses now.
    [Fact]
    public async Task A_save_under_SuppressReplication_replicates_nothing_and_leaves_the_row_dirty()
    {
        await using var host = ItemHost();
        await using var scope = host.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Db>();

        using (db.SuppressReplication())
        {
            db.Items.Add(new Item { Code = "a", Region = "north" });
            await db.SaveChangesAsync(Ct);
        }

        db.Notes.Add(new Note { Text = "a later save, outside the scope" });
        await db.SaveChangesAsync(Ct);
        await host.Log.AllFinishedAsync();

        Assert.Empty(host.Log.Started);
        Assert.Empty(host.Cosmos.Writes);
        Assert.Null((await host.StoredAsync<Item>(1)).LastReplicationDate);

        // The scope ended, so the next save of the row replicates it.
        var item = await db.Items.SingleAsync(Ct);
        item.Code = "b";
        await db.SaveChangesAsync(Ct);
        await host.Log.AllFinishedAsync();

        Assert.Equal(["Item 1"], host.Log.Started);
    }

    [Fact]
    public async Task A_refused_document_fails_the_sync_and_leaves_the_row_dirty()
    {
        await using var host = ItemHost();
        host.Cosmos.Refuse = _ => true;
        await using var scope = host.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Db>();

        db.Items.Add(new Item { Code = "a", Region = "north" });
        await db.SaveChangesAsync(Ct);
        await host.Log.AllFinishedAsync();

        Assert.Equal([NorthA], host.Cosmos.WriteLines);
        Assert.Single(host.Log.Failed);
        var stored = await host.StoredAsync<Item>(1);
        Assert.Equal(((DateTimeOffset?)null, (string?)null), (stored.LastReplicationDate, stored.LastReplicationStamp));
    }

    // A host's handler that numbers new items after the insert, with a second save on the same context, inside the
    // save's transaction.
    /// <summary>When item "a" is added, adds a second item to the same save.</summary>
    public sealed class TwinHandler : IBeforeSaveHandler<Item>
    {
        public ValueTask BeforeSaveAsync(PendingRows<Item> rows, CancellationToken cancellationToken)
        {
            foreach (var row in rows)
                if (row.Change == RowChange.Added && row.Entity.Code == "a")
                    rows.Db.Add(new Item { Code = "twin of a", Region = row.Entity.Region });

            return ValueTask.CompletedTask;
        }
    }

    // A row that a before-save handler adds is written by the save, so it is stamped and replicated as any other row
    // of the save. A row that a trigger added was written unstamped.
    [Fact]
    public async Task A_row_that_a_before_save_handler_adds_is_stamped_and_replicated()
    {
        await using var host = ItemHost(services => services.AddSaveHandler<TwinHandler>());
        await using var scope = host.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Db>();

        db.Items.Add(new Item { Code = "a", Region = "north" });
        await db.SaveChangesAsync(Ct);
        await host.Log.AllFinishedAsync();

        host.Log.AssertNoneFailed();
        Assert.Equal(["Item 1", "Item 2"], host.Log.Started.Order());
        Assert.Equal(2, host.Cosmos.WriteLines.Count);
        Assert.Contains(host.Cosmos.WriteLines, line => line.Contains("\"Code\":\"twin of a\""));

        await using var bare = host.Bare();
        var twin = await bare.Items.AsNoTracking().SingleAsync(x => x.Code == "twin of a", Ct);

        Assert.NotEqual(default, twin.CreateDate);
        Assert.Equal((DateTimeOffset?)twin.LastSaveDate, twin.LastReplicationDate);
    }

    public sealed class NumberingHandler : IAfterSaveHandler<Item>
    {
        public async ValueTask AfterSaveAsync(SavedRows<Item> rows, CancellationToken cancellationToken)
        {
            var db = (Db)rows.Db;
            var numbered = false;

            foreach (var row in rows)
            {
                if (row.Change != RowChange.Added)
                    continue;

                row.Entity.Rank = await db.Items.Where(x => x.ID != row.Entity.ID).CountAsync(cancellationToken) + 1;
                numbered = true;
            }

            if (numbered)
                await db.SaveChangesAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task A_row_that_an_after_save_handler_saves_again_is_replicated_once_with_that_change()
    {
        await using var host = ItemHost(services => services.AddSaveHandler<NumberingHandler>());
        await using var scope = host.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Db>();

        db.Items.Add(new Item { Code = "a", Region = "north" });
        await db.SaveChangesAsync(Ct);
        await host.Log.AllFinishedAsync();

        // Both saves ran in the one transaction the context opened for the first, so their syncs of the row started
        // together at its commit, as one: the insert of the row as it was committed.
        var stored = await host.StoredAsync<Item>(1);
        Assert.Equal(1, stored.Rank);
        Assert.Equal(
        [
            """upsert items/1 {"id":"1","Region":"north","ItemType":"Item","Rank":1,"Code":"a","CategoryID":null,"CategoryName":null,"Category":null}""",
        ], host.Cosmos.WriteLines);
        Assert.Equal(["Item 1"], host.Log.Started);
        host.Log.AssertNoneFailed();
    }

    // A host's handler that tidies an item before it is written.
    public sealed class TrimmingHandler : IBeforeSaveHandler<Item>
    {
        public ValueTask BeforeSaveAsync(PendingRows<Item> rows, CancellationToken cancellationToken)
        {
            foreach (var row in rows)
                if (row.Change != RowChange.Deleted)
                    row.Entity.Code = row.Entity.Code.Trim();

            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task What_a_before_save_handler_changes_reaches_the_document()
    {
        await using var host = ItemHost(services => services.AddSaveHandler<TrimmingHandler>());
        await using var scope = host.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Db>();

        var item = new Item { Code = " a ", Region = "north" };
        db.Items.Add(item);
        db.Notes.Add(new Note { Text = "not replicated" });
        await db.SaveChangesAsync(Ct);
        await host.Log.AllFinishedAsync();

        item.Code = " b ";
        await db.SaveChangesAsync(Ct);
        await host.Log.AllFinishedAsync();

        Assert.Equal(
        [
            NorthA,
            """upsert items/1 {"id":"1","Region":"north","ItemType":"Item","Rank":0,"Code":"b","CategoryID":null,"CategoryName":null,"Category":null}""",
        ], host.Cosmos.WriteLines);
    }

    [Fact]
    public async Task Neither_path_changes_an_audit_column()
    {
        var saved = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
        var created = new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);

        await using var host = ItemHost(services => services.AddShiftEntityCosmosDbReplication<Db>());
        await using var scope = host.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Db>();

        // The save path. The values are set by hand, so the context's insert backfill keeps them.
        db.Items.Add(new Item
        {
            Code = "a", Region = "north", CreateDate = created, CreatedByUserID = 5, LastSaveDate = saved, LastSavedByUserID = 6,
        });
        await db.SaveChangesAsync(Ct);
        await host.Log.AllFinishedAsync();

        // The catch-up path, on a dirty row written through a context that replicates nothing.
        await using (var setup = host.Bare())
        {
            setup.Items.Add(new Item
            {
                Code = "b", Region = "north", CreateDate = created, CreatedByUserID = 5, LastSaveDate = saved, LastSavedByUserID = 6,
            });
            await setup.SaveChangesAsync(Ct);
        }

        await scope.ServiceProvider.GetRequiredService<CosmosDBReplication>()
            .SetUp<Db, Item>(host.Cosmos.Client, ReplicationPinHost.Database)
            .Replicate<ItemDocument>("items", Document)
            .RunAndReportAsync();

        foreach (var id in new long[] { 1, 2 })
        {
            var stored = await host.StoredAsync<Item>(id);
            Assert.Equal((created, (long?)5, saved, (long?)6, false),
                (stored.CreateDate, stored.CreatedByUserID, stored.LastSaveDate, stored.LastSavedByUserID, stored.IsDeleted));
            Assert.Equal((DateTimeOffset?)saved, stored.LastReplicationDate);
        }
    }
}
