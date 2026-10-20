using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.CosmosDbReplication;
using ShiftSoftware.ShiftEntity.Model.Enums;
using Xunit;
using static ShiftSoftware.ShiftEntity.Tests.Replication.PinModel;

namespace ShiftSoftware.ShiftEntity.Tests.Replication;

/// <summary>
/// Pins a save-time sync whose step reports failure rather than throwing: a stale document it could not delete, an
/// upsert or a reference update that failed. Such a sync used to be logged as succeeded, because only an exception
/// counted as a failure. It is logged as failed, with the row and the reason, and records nothing, so the row stays
/// due for the catch-up. A delete the stand-in refuses is such a step: the sync catches it and reports it.
/// </summary>
public class ReplicationFailedStepTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_document_that_cannot_be_deleted_from_its_old_partition_fails_the_sync_with_the_reason()
    {
        await using var host = new ReplicationPinHost(
            (o, client) => o.SetUpReplication<Db, Item>(client, ReplicationPinHost.Database)
                .Replicate<ItemDocument>("items", x => x.Region, x => Document(x.Entity)),
            containers: ("items", ["/Region"]));

        await using var scope = host.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Db>();
        var item = new Item { Code = "a", Region = "north" };
        db.Items.Add(item);
        await db.SaveChangesAsync(Ct);
        await host.Log.AllFinishedAsync();
        var firstVersion = item.LastSaveDate;

        host.Cosmos.Refuse = x => x.Kind == "delete";
        item.Region = "south";      // a new partition key moves the document
        item.LastSaveDate = firstVersion.AddMinutes(1);     // as a repository save stamps it
        await db.SaveChangesAsync(Ct);
        await host.Log.AllFinishedAsync();

        // Nothing is written under the new key while the old document stays, and nothing is recorded.
        Assert.Equal(["upsert items/1", "delete items/1"], host.Cosmos.Writes.Select(x => $"{x.Kind} {x.Container}/{x.Id}"));
        var failed = Assert.Single(host.Log.Failed);
        Assert.Contains("With ID: 1", failed);
        Assert.Contains("the document the last sync wrote ('1' in 'items') could not be deleted, so the new one was not written: " +
            "Cosmos DB answered 503", failed);
        var stored = await host.StoredAsync<Item>(1);
        Assert.Equal((DateTimeOffset?)firstVersion, stored.LastReplicationDate);
        Assert.Equal("north", LastReplicationStamp.Deserialize(stored.LastReplicationStamp)?.Level1?.Value);
    }

    [Fact]
    public async Task A_tree_document_that_cannot_replace_the_last_one_fails_the_sync_with_the_reason()
    {
        await using var host = new ShopTreeHost();
        await host.SeedAsync();

        // The document the last sync wrote had another id, so each sync first deletes it, and that delete is refused.
        var moved = new LastReplicationStamp
        {
            Id = "old-1",
            Level1 = new PartitionKeyLevelStamp { Value = "old-1", Type = PartitionKeyTypes.String },
        }.Serialize();
        await using (var db = host.Bare())
            await db.Shops.Where(x => x.ID == 1).ExecuteUpdateAsync(s => s.SetProperty(x => x.LastReplicationStamp, moved), Ct);
        host.Cosmos.Refuse = x => x.Kind == "delete";

        await host.SaveAsync(async db => (await db.Owners.SingleAsync(x => x.ID == 1, Ct)).Name = "Ana");     // a part of shop 1
        await host.SaveAsync(async db => (await db.Shops.SingleAsync(x => x.ID == 1, Ct)).Name = "Corner 2");   // shop 1 itself

        Assert.Equal(["Shop 1", "Shop 1"], host.Log.Started);
        Assert.Equal(2, host.Log.Failed.Count);
        Assert.All(host.Log.Failed, x =>
        {
            Assert.Contains("With ID: 1", x);
            Assert.Contains("the document the last sync wrote ('old-1' in 'shops') could not be deleted", x);
        });
        Assert.Equal(["delete shops/old-1", "delete shops/old-1"], host.Cosmos.Writes.Select(x => $"{x.Kind} {x.Container}/{x.Id}"));
        Assert.Equal("[1]", await host.MarkedAsync());
        Assert.Equal(moved, (await host.StoredAsync(1)).LastReplicationStamp);
    }
}
