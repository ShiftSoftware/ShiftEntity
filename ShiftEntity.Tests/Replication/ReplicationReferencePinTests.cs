using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.CosmosDbReplication.Services;
using Xunit;
using static ShiftSoftware.ShiftEntity.Tests.Replication.PinModel;

namespace ShiftSoftware.ShiftEntity.Tests.Replication;

/// <summary>
/// Pins reference updates: <c>UpdateReference</c>, which merges the saved row onto every document that refers to it,
/// and <c>UpdatePropertyReference</c>, which patches one member of those documents. The after-save trigger runs them
/// for an edit only, never for an insert or a delete, after the row's own document and in the order they were
/// registered. The pins fix the queries that find the documents (the SQL text the finders produce) and the bodies of
/// the writes, on the trigger path and on the catch-up path, and what a refused reference write leaves on the row.
/// </summary>
public class ReplicationReferencePinTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The document of item 7, which refers to category 1 twice: by its id and name, and by an embedded reference.
    private static ItemDocument Referring() => new()
    {
        id = "7",
        Region = "north",
        Rank = 1,
        Code = "drill",
        CategoryID = "1",
        CategoryName = "Tools",
        Category = new CategoryReference { id = "1", Name = "Tools" },
    };

    private static ReplicationPinHost TriggerHost() => new(
        (o, client) => o.SetUpReplication<Db, Category>(client, ReplicationPinHost.Database)
            .Replicate<CategoryDocument>("categories", x => x.id,
                x => new CategoryDocument { id = x.Entity.ID.ToString(), Name = x.Entity.Name })
            .UpdateReference<ItemDocument>("items",
                (q, e) => q.Where(d => d.CategoryID == e.Entity.ID.ToString()),
                (e, d) => { d.CategoryName = e.Entity.Name; return d; })
            .UpdatePropertyReference<CategoryReference, ItemDocument>("items", d => d.Category!,
                (q, e) => q.Where(d => d.Category!.id == e.Entity.ID.ToString()),
                e => new CategoryReference { id = e.Entity.ID.ToString(), Name = e.Entity.Name }),
        containers: [("categories", ["/id"]), ("items", ["/Region", "/ItemType"])]);

    [Fact]
    public async Task An_edit_updates_the_references_after_the_rows_own_document_and_an_insert_or_delete_does_not()
    {
        await using var host = TriggerHost();
        host.Cosmos.Seed("items", Referring());

        await using var scope = host.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Db>();
        var category = new Category { Name = "Tools" };
        db.Categories.Add(category);
        await db.SaveChangesAsync(Ct);
        await host.Log.AllFinishedAsync();

        Assert.Equal(["""upsert categories/1 {"id":"1","Name":"Tools"}"""], host.Cosmos.WriteLines);
        Assert.Empty(host.Cosmos.Queries);

        category.Name = "Gear";
        await db.SaveChangesAsync(Ct);
        await host.Log.AllFinishedAsync();

        db.Categories.Remove(category);
        await db.SaveChangesAsync(Ct);
        await host.Log.AllFinishedAsync();

        Assert.Equal(
        [
            """upsert categories/1 {"id":"1","Name":"Tools"}""",
            """upsert categories/1 {"id":"1","Name":"Gear"}""",
            """upsert items/7 {"id":"7","Region":"north","ItemType":"Item","Rank":1,"Code":"drill","CategoryID":"1","CategoryName":"Gear","Category":{"id":"1","Name":"Tools"}}""",
            """patch items/7 ["north","Item"] [{"op":"Replace","path":"/Category","value":{"id":"1","Name":"Gear"}}]""",
            """delete categories/1 ["1"]""",
        ], host.Cosmos.WriteLines);
        Assert.Equal(
        [
            ("items", """SELECT VALUE root FROM root WHERE (root["CategoryID"] = "1")"""),
            ("items", """SELECT VALUE root FROM root WHERE (root["Category"]["id"] = "1")"""),
        ], host.Cosmos.Queries);
        Assert.Equal(["Category 1", "Category 1", "Category 1"], host.Log.Started);
        Assert.Empty(host.Log.Failed);

        // What the two updates left on the referring document, and the category's own document gone.
        Assert.Equal(
        [
            """{"id":"7","Region":"north","ItemType":"Item","Rank":1,"Code":"drill","CategoryID":"1","CategoryName":"Gear","Category":{"id":"1","Name":"Gear"}}""",
        ], host.Cosmos.StoredLines("items"));
        Assert.Empty(host.Cosmos.Stored("categories"));
        Assert.False(await host.ExistsAsync<Category>(category.ID));
    }

    [Fact]
    public async Task An_edit_that_finds_no_referring_document_writes_only_the_rows_own_document()
    {
        await using var host = TriggerHost();

        await using var scope = host.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Db>();
        var category = new Category { Name = "Tools" };
        db.Categories.Add(category);
        await db.SaveChangesAsync(Ct);
        await host.Log.AllFinishedAsync();

        category.Name = "Gear";
        await db.SaveChangesAsync(Ct);
        await host.Log.AllFinishedAsync();

        Assert.Equal(
        [
            """upsert categories/1 {"id":"1","Name":"Tools"}""",
            """upsert categories/1 {"id":"1","Name":"Gear"}""",
        ], host.Cosmos.WriteLines);
        Assert.Equal(2, host.Cosmos.Queries.Count);

        // Both steps succeeded, so the edit's sync recorded its watermark.
        var stored = await host.StoredAsync<Category>(category.ID);
        Assert.Equal((DateTimeOffset?)category.LastSaveDate, stored.LastReplicationDate);
    }

    // A step that reports failure, rather than throwing, used to be logged as a success.
    [Fact]
    public async Task A_refused_reference_write_leaves_the_row_dirty_and_the_sync_logs_its_failure()
    {
        await using var host = TriggerHost();
        host.Cosmos.Seed("items", Referring());

        await using var scope = host.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Db>();
        var category = new Category { Name = "Tools" };
        db.Categories.Add(category);
        await db.SaveChangesAsync(Ct);
        await host.Log.AllFinishedAsync();
        var firstVersion = category.LastSaveDate;

        host.Cosmos.Refuse = x => x.Kind == "patch";
        category.Name = "Gear";
        category.LastSaveDate = firstVersion.AddMinutes(1);    // as a repository save stamps it; a bare context does not
        await db.SaveChangesAsync(Ct);
        await host.Log.AllFinishedAsync();

        // The row's own document and the merge were written, the patch was refused, and nothing was recorded: the
        // watermark is still the first version's, so the catch-up selects the row again. The sync says why it failed.
        Assert.Equal(4, host.Cosmos.Writes.Count);
        var stored = await host.StoredAsync<Category>(category.ID);
        Assert.Equal((DateTimeOffset?)firstVersion, stored.LastReplicationDate);
        Assert.True(stored.LastReplicationDate < stored.LastSaveDate);
        var failed = Assert.Single(host.Log.Failed);
        Assert.Contains($"With ID: {category.ID}", failed);
        Assert.Contains("a document in 'items' that refers to the row could not be updated: Cosmos DB answered 503", failed);
    }

    [Fact]
    public async Task The_catch_up_updates_the_references_of_every_row_it_selects()
    {
        await using var host = new ReplicationPinHost(null,
            services => services.AddShiftEntityCosmosDbReplication<Db>(),
            containers: [("categories", ["/id"]), ("items", ["/Region", "/ItemType"])]);
        host.Cosmos.Seed("items", Referring());

        await using (var setup = host.Bare())
        {
            setup.Categories.Add(new Category { Name = "Gear" });
            await setup.SaveChangesAsync(Ct);
        }

        await using var scope = host.Provider.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<CosmosDBReplication>()
            .SetUp<Db, Category>(host.Cosmos.Client, ReplicationPinHost.Database)
            .Replicate<CategoryDocument>("categories", x => new CategoryDocument { id = x.ID.ToString(), Name = x.Name })
            .UpdateReference<ItemDocument>("items",
                (q, e) => q.Where(d => d.CategoryID == e.ID.ToString()),
                (e, d) => { d.CategoryName = e.Name; return d; })
            .UpdatePropertyReference<CategoryReference, ItemDocument>("items", d => d.Category!,
                (q, e) => q.Where(d => d.Category!.id == e.ID.ToString()),
                e => new CategoryReference { id = e.ID.ToString(), Name = e.Name })
            .RunAndReportAsync();

        Assert.Equal((1, 1, 0), (result.Selected, result.Replicated, result.Failed));
        Assert.Equal(
        [
            """upsert categories/1 {"id":"1","Name":"Gear"}""",
            """upsert items/7 {"id":"7","Region":"north","ItemType":"Item","Rank":1,"Code":"drill","CategoryID":"1","CategoryName":"Gear","Category":{"id":"1","Name":"Tools"}}""",
            """patch items/7 ["north","Item"] [{"op":"Replace","path":"/Category","value":{"id":"1","Name":"Gear"}}]""",
        ], host.Cosmos.WriteLines);
        Assert.Equal(
        [
            ("items", """SELECT VALUE root FROM root WHERE (root["CategoryID"] = "1")"""),
            ("items", """SELECT VALUE root FROM root WHERE (root["Category"]["id"] = "1")"""),
        ], host.Cosmos.Queries);

        var stored = await host.StoredAsync<Category>(1);
        Assert.Equal((DateTimeOffset?)stored.LastSaveDate, stored.LastReplicationDate);
    }
}
