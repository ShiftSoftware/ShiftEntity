using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.CosmosDbReplication;
using ShiftSoftware.ShiftEntity.CosmosDbReplication.Services;
using ShiftSoftware.ShiftEntity.Model.Enums;
using Xunit;
using static ShiftSoftware.ShiftEntity.Tests.Replication.PinModel;

namespace ShiftSoftware.ShiftEntity.Tests.Replication;

/// <summary>
/// Pins what replication does with a row whose stamp describes a document in another container, as every row's stamp
/// does when its registration moves to a new container: a branch's stamp still describes its document in a container
/// partitioned by two levels after the branch moved to one partitioned by <c>/id</c>. That document is not in the new
/// container, so nothing is deleted for it, the row is written there, and its stamp then describes the new document.
/// Cosmos DB refuses a key of the wrong shape (the stand-in does too), and the delete used to fail such a row on every
/// sync: on a save nothing was written, yet the sync logged success, and the catch-up failed the row on every run.
/// </summary>
public class ReplicationStaleStampTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The stamp of a branch's document in a container partitioned by its branch ID and its item type.
    private static string TwoLevelStamp(long id) => new LastReplicationStamp
    {
        Id = id.ToString(),
        Level1 = new PartitionKeyLevelStamp { Value = id.ToString(), Type = PartitionKeyTypes.String },
        Level2 = new PartitionKeyLevelStamp { Value = "Branch", Type = PartitionKeyTypes.String },
    }.Serialize();

    private static void AssertStampOfOneLevel(string? stamp, long id)
    {
        var parsed = LastReplicationStamp.Deserialize(stamp);
        Assert.NotNull(parsed);
        Assert.Equal((id.ToString(), id.ToString()), (parsed.Id, parsed.Level1?.Value));
        Assert.Null(parsed.Level2);
    }

    private static ReplicationPinHost ItemHost(params (string Id, string[] PartitionKeyPaths)[] containers) => new(
        (o, client) => o.SetUpReplication<Db, Item>(client, ReplicationPinHost.Database)
            .Replicate<ItemDocument>("items", x => x.id, x => Document(x.Entity)),
        services => services.AddShiftEntityCosmosDbReplication<Db>(),
        null, containers.Length > 0 ? containers : [("items", ["/id"])]);

    // A row recorded as replicated, with the stamp of a document in another container.
    private static async Task SeedAsync(ReplicationPinHost host, string stamp)
    {
        await using var db = host.Bare();
        db.Items.Add(new Item { Code = "a", Region = "north" });
        await db.SaveChangesAsync(Ct);
        await db.Items.ExecuteUpdateAsync(s => s
            .SetProperty(x => x.LastReplicationDate, x => (DateTimeOffset?)x.LastSaveDate)
            .SetProperty(x => x.LastReplicationStamp, stamp), Ct);
    }

    [Fact]
    public async Task A_save_writes_the_row_to_its_new_container_and_deletes_nothing_for_a_stamp_of_another()
    {
        await using var host = ItemHost();
        await SeedAsync(host, TwoLevelStamp(1));

        await using (var scope = host.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Db>();
            (await db.Items.SingleAsync(Ct)).Code = "b";
            await db.SaveChangesAsync(Ct);
        }
        await host.Log.AllFinishedAsync();

        Assert.Equal(["""upsert items/1 {"id":"1","Region":"north","ItemType":"Item","Rank":0,"Code":"b","CategoryID":null,"CategoryName":null,"Category":null}"""],
            host.Cosmos.WriteLines);
        Assert.Empty(host.Log.Failed);
        var stored = await host.StoredAsync<Item>(1);
        Assert.Equal((DateTimeOffset?)stored.LastSaveDate, stored.LastReplicationDate);
        AssertStampOfOneLevel(stored.LastReplicationStamp, 1);
    }

    [Fact]
    public async Task A_hard_delete_deletes_nothing_for_a_stamp_of_another_container()
    {
        await using var host = ItemHost();
        await SeedAsync(host, TwoLevelStamp(1));

        await using (var scope = host.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Db>();
            db.Items.Remove(await db.Items.SingleAsync(Ct));
            await db.SaveChangesAsync(Ct);
        }
        await host.Log.AllFinishedAsync();

        Assert.Equal(["Item 1"], host.Log.Started);
        Assert.Empty(host.Log.Failed);
        Assert.Empty(host.Cosmos.Writes);
    }

    [Fact]
    public async Task A_tree_document_is_written_to_its_new_container_and_nothing_is_deleted_for_a_stamp_of_another()
    {
        // On disk: the three syncs of the save run at once, and an in-memory connection cannot be shared by them.
        await using var host = new ShopTreeHost(onDisk: true);
        await host.SeedAsync();
        await using (var db = host.Bare())
            foreach (var id in new[] { 1L, 2L })
                await db.Shops.Where(x => x.ID == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.LastReplicationStamp, TwoLevelStamp(id)), Ct);

        await host.SaveAsync(async db =>
        {
            (await db.Shops.SingleAsync(x => x.ID == 1, Ct)).Name = "Corner 2";    // the root's own save
            (await db.Owners.SingleAsync(x => x.ID == 2, Ct)).Name = "Bea";        // a part of shops 2 and 3
        });

        Assert.Equal(["Shop 1", "Shop 2", "Shop 3"], host.Log.Started.Order());
        Assert.Empty(host.Log.Failed);
        Assert.Equal(["upsert shops/1", "upsert shops/2", "upsert shops/3"],
            host.Cosmos.Writes.Select(x => $"{x.Kind} {x.Container}/{x.Id}").Order());
        foreach (var id in new[] { 1L, 2L })
            AssertStampOfOneLevel((await host.StoredAsync(id)).LastReplicationStamp, id);
        Assert.Equal("[]", await host.MarkedAsync());
    }

    // The forced catch-up that fills a new container, for a row replicated before as a document of its own and for a
    // tree's root.
    [Fact]
    public async Task The_catch_up_writes_the_rows_to_their_new_container_and_deletes_nothing_for_stamps_of_another()
    {
        await using var host = ItemHost();
        await SeedAsync(host, TwoLevelStamp(1));

        await using (var scope = host.Provider.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<CosmosDBReplication>()
                .SetUp<Db, Item>(host.Cosmos.Client, ReplicationPinHost.Database)
                .Replicate<ItemDocument>("items", Document)
                .RunAndReportAsync(updateAll: true);
            Assert.Equal((1, 1, 0), (result.Selected, result.Replicated, result.Failed));
        }
        Assert.Equal(["upsert items/1"], host.Cosmos.Writes.Select(x => $"{x.Kind} {x.Container}/{x.Id}"));
        AssertStampOfOneLevel((await host.StoredAsync<Item>(1)).LastReplicationStamp, 1);

        await using var shops = new ShopTreeHost();
        await shops.SeedAsync();
        await using (var db = shops.Bare())
            foreach (var id in new[] { 1L, 2L, 3L })
                await db.Shops.Where(x => x.ID == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.LastReplicationStamp, TwoLevelStamp(id)), Ct);

        var trees = await shops.CatchUpAsync(updateAll: true);
        Assert.Equal((3, 3, 0), (trees.Selected, trees.Replicated, trees.Failed));
        Assert.Equal(["upsert shops/1", "upsert shops/2", "upsert shops/3"],
            shops.Cosmos.Writes.Select(x => $"{x.Kind} {x.Container}/{x.Id}").Order());
        foreach (var id in new[] { 1L, 2L, 3L })
            AssertStampOfOneLevel((await shops.StoredAsync(id)).LastReplicationStamp, id);
    }

    // The other way round: a stamp of one level, against a container partitioned by two.
    [Fact]
    public async Task The_catch_up_deletes_nothing_for_a_stamp_with_fewer_levels_than_the_container()
    {
        await using var host = ItemHost(("items", ["/Region", "/ItemType"]));
        await SeedAsync(host, new LastReplicationStamp
        {
            Id = "1",
            Level1 = new PartitionKeyLevelStamp { Value = "1", Type = PartitionKeyTypes.String },
        }.Serialize());

        await using (var scope = host.Provider.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<CosmosDBReplication>()
                .SetUp<Db, Item>(host.Cosmos.Client, ReplicationPinHost.Database)
                .Replicate<ItemDocument>("items", Document)
                .RunAndReportAsync(updateAll: true);
            Assert.Equal((1, 1, 0), (result.Selected, result.Replicated, result.Failed));
        }
        Assert.Equal(["upsert items/1"], host.Cosmos.Writes.Select(x => $"{x.Kind} {x.Container}/{x.Id}"));
        var stamp = LastReplicationStamp.Deserialize((await host.StoredAsync<Item>(1)).LastReplicationStamp);
        Assert.Equal(("north", "Item"), (stamp?.Level1?.Value, stamp?.Level2?.Value));
    }
}
