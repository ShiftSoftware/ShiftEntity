using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShiftMapper;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Model.Enums;
using Xunit;
using static ShiftSoftware.ShiftEntity.Tests.Replication.PinModel;

namespace ShiftSoftware.ShiftEntity.Tests.Replication;

/// <summary>
/// Pins the documents the after-save trigger writes for each shape of registration: through the host's mapper (no
/// delegate), through a mapping delegate, with a preparer, with a set-up mapping, and with one, two and three partition
/// key levels. For each shape it pins an insert, an edit, an edit that moves the document, and a hard delete: the
/// writes, in order, with ids, partition keys and bodies. Written against the engine as it was before the replication
/// engine moved off the trigger library, so the new engine must give the same lines.
/// </summary>
public class ReplicationDocumentPinTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Saves an item through a context of the caller's own scope, then waits for its sync.
    private static async Task<Item> AddAsync(ReplicationPinHost host, AsyncServiceScope scope, Item item)
    {
        var db = scope.ServiceProvider.GetRequiredService<Db>();
        db.Items.Add(item);
        await db.SaveChangesAsync(Ct);
        await host.Log.AllFinishedAsync();
        return item;
    }

    private static async Task SaveAsync(ReplicationPinHost host, AsyncServiceScope scope)
    {
        await scope.ServiceProvider.GetRequiredService<Db>().SaveChangesAsync(Ct);
        await host.Log.AllFinishedAsync();
    }

    [Fact]
    public async Task Without_a_delegate_the_hosts_mapper_writes_the_document()
    {
        await using var host = new ReplicationPinHost(
            (o, client) => o.SetUpReplication<Db, Item>(client, ReplicationPinHost.Database)
                .Replicate<ItemDocument>("items", x => x.Region),
            services => services.AddSingleton<IMapper>(new PinMapper().Create<Item, ItemDocument>(Document)),
            containers: ("items", ["/Region"]));

        await using var scope = host.Provider.CreateAsyncScope();
        var item = await AddAsync(host, scope, new Item { Code = "a", Region = "north", Rank = 3 });

        item.Code = "b";
        await SaveAsync(host, scope);

        item.Region = "south";      // a new partition key moves the document
        await SaveAsync(host, scope);

        scope.ServiceProvider.GetRequiredService<Db>().Items.Remove(item);      // a hard delete
        await SaveAsync(host, scope);

        Assert.Equal(
        [
            """upsert items/1 {"id":"1","Region":"north","ItemType":"Item","Rank":3,"Code":"a","CategoryID":null,"CategoryName":null,"Category":null}""",
            """upsert items/1 {"id":"1","Region":"north","ItemType":"Item","Rank":3,"Code":"b","CategoryID":null,"CategoryName":null,"Category":null}""",
            """delete items/1 ["north"]""",
            """upsert items/1 {"id":"1","Region":"south","ItemType":"Item","Rank":3,"Code":"b","CategoryID":null,"CategoryName":null,"Category":null}""",
            """delete items/1 ["south"]""",
        ], host.Cosmos.WriteLines);
        Assert.Equal(["Item 1", "Item 1", "Item 1", "Item 1"], host.Log.Started);
        Assert.Empty(host.Log.Failed);
    }

    [Fact]
    public async Task A_mapping_delegate_writes_the_document_under_two_partition_key_levels()
    {
        await using var host = new ReplicationPinHost(
            (o, client) => o.SetUpReplication<Db, Item>(client, ReplicationPinHost.Database)
                .Replicate<ItemDocument>("items", x => x.Region, x => x.ItemType, x => Document(x.Entity)),
            containers: ("items", ["/Region", "/ItemType"]));

        await using var scope = host.Provider.CreateAsyncScope();
        var item = await AddAsync(host, scope, new Item { Code = "a", Region = "north", Rank = 3 });

        item.Region = "south";
        await SaveAsync(host, scope);

        scope.ServiceProvider.GetRequiredService<Db>().Items.Remove(item);
        await SaveAsync(host, scope);

        Assert.Equal(
        [
            """upsert items/1 {"id":"1","Region":"north","ItemType":"Item","Rank":3,"Code":"a","CategoryID":null,"CategoryName":null,"Category":null}""",
            """delete items/1 ["north","Item"]""",
            """upsert items/1 {"id":"1","Region":"south","ItemType":"Item","Rank":3,"Code":"a","CategoryID":null,"CategoryName":null,"Category":null}""",
            """delete items/1 ["south","Item"]""",
        ], host.Cosmos.WriteLines);
        Assert.Empty(host.Log.Failed);
    }

    [Fact]
    public async Task Three_partition_key_levels_with_a_number_among_them()
    {
        await using var host = new ReplicationPinHost(
            (o, client) => o.SetUpReplication<Db, Item>(client, ReplicationPinHost.Database)
                .Replicate<ItemDocument>("items", x => x.Region, x => x.Rank, x => x.ItemType, x => Document(x.Entity)),
            containers: ("items", ["/Region", "/Rank", "/ItemType"]));

        await using var scope = host.Provider.CreateAsyncScope();
        var item = await AddAsync(host, scope, new Item { Code = "a", Region = "north", Rank = 3 });

        item.Rank = 4;
        await SaveAsync(host, scope);

        scope.ServiceProvider.GetRequiredService<Db>().Items.Remove(item);
        await SaveAsync(host, scope);

        Assert.Equal(
        [
            """upsert items/1 {"id":"1","Region":"north","ItemType":"Item","Rank":3,"Code":"a","CategoryID":null,"CategoryName":null,"Category":null}""",
            """delete items/1 ["north",3.0,"Item"]""",
            """upsert items/1 {"id":"1","Region":"north","ItemType":"Item","Rank":4,"Code":"a","CategoryID":null,"CategoryName":null,"Category":null}""",
            """delete items/1 ["north",4.0,"Item"]""",
        ], host.Cosmos.WriteLines);
        Assert.Empty(host.Log.Failed);
    }

    // Loads the item's category onto the entity the way Identity's link preparers load the service, department or
    // brand: a tracking query on the context it is given, which in the trigger is the caller's own context, because
    // the trigger is resolved in the caller's scope. Records what it was given.
    public sealed class CategoryPreparer(Db db) : IShiftEntityPrepareForReplicationAsync<Item>
    {
        public static readonly List<string> Calls = new();

        public async ValueTask<Item> PrepareForReplicationAsync(Item entity, ReplicationChangeType changeType)
        {
            lock (Calls)
                Calls.Add($"{changeType} {entity.ID} {entity.Code}");

            entity.Category = await db.Categories.SingleOrDefaultAsync(x => x.ID == entity.CategoryID);

            return entity;
        }
    }

    [Fact]
    public async Task A_preparer_runs_before_the_mapping_with_the_change_type_and_its_entity_is_mapped()
    {
        lock (CategoryPreparer.Calls)
            CategoryPreparer.Calls.Clear();

        await using var host = new ReplicationPinHost(
            (o, client) => o.SetUpReplication<Db, Item>(client, ReplicationPinHost.Database)
                .Replicate<ItemDocument>("items", x => x.id, x => Document(x.Entity)),
            services => services.AddScoped<IShiftEntityPrepareForReplicationAsync<Item>, CategoryPreparer>(),
            containers: ("items", ["/id"]));

        await using (var setup = host.Bare())
        {
            setup.Categories.Add(new Category { Name = "Tools" });
            await setup.SaveChangesAsync(Ct);
        }

        await using var scope = host.Provider.CreateAsyncScope();
        var item = await AddAsync(host, scope, new Item { Code = "a", Region = "north", CategoryID = 1 });

        item.Code = "b";
        await SaveAsync(host, scope);

        scope.ServiceProvider.GetRequiredService<Db>().Items.Remove(item);
        await SaveAsync(host, scope);

        Assert.Equal(["Added 1 a", "Modified 1 b", "Deleted 1 b"], CategoryPreparer.Calls);
        Assert.Equal(
        [
            """upsert items/1 {"id":"1","Region":"north","ItemType":"Item","Rank":0,"Code":"a","CategoryID":"1","CategoryName":"Tools","Category":null}""",
            """upsert items/1 {"id":"1","Region":"north","ItemType":"Item","Rank":0,"Code":"b","CategoryID":"1","CategoryName":"Tools","Category":null}""",
            """delete items/1 ["1"]""",
        ], host.Cosmos.WriteLines);

        // The preparer set the navigation on the caller's own instance (correction C5 is about this).
        Assert.Equal("Tools", item.Category?.Name);
        Assert.Empty(host.Log.Failed);
    }

    [Fact]
    public async Task A_set_up_mapping_runs_first_and_its_entity_is_replicated()
    {
        await using var host = new ReplicationPinHost(
            (o, client) => o.SetUpReplication<Db, Item>(client, ReplicationPinHost.Database,
                    x => new ValueTask<Item>(new Item
                    {
                        ID = x.Entity.ID,
                        Code = x.Entity.Code.ToUpperInvariant(),
                        Region = x.Entity.Region,
                        LastSaveDate = x.Entity.LastSaveDate,
                        LastReplicationStamp = x.Entity.LastReplicationStamp,
                    }))
                .Replicate<ItemDocument>("items", x => x.id, x => Document(x.Entity)),
            containers: ("items", ["/id"]));

        await using var scope = host.Provider.CreateAsyncScope();
        var item = await AddAsync(host, scope, new Item { Code = "a", Region = "north" });

        Assert.Equal(
        [
            """upsert items/1 {"id":"1","Region":"north","ItemType":"Item","Rank":0,"Code":"A","CategoryID":null,"CategoryName":null,"Category":null}""",
        ], host.Cosmos.WriteLines);

        // The sync recorded the watermark of the version it replicated, by key.
        var stored = await host.StoredAsync<Item>(item.ID);
        Assert.Equal((DateTimeOffset?)item.LastSaveDate, stored.LastReplicationDate);
        Assert.Equal("a", item.Code);
        Assert.Empty(host.Log.Failed);
    }

    [Fact]
    public async Task Only_rows_of_a_registered_type_start_a_sync()
    {
        await using var host = new ReplicationPinHost(
            (o, client) => o.SetUpReplication<Db, Item>(client, ReplicationPinHost.Database)
                .Replicate<ItemDocument>("items", x => x.id, x => Document(x.Entity)),
            containers: ("items", ["/id"]));

        await using var scope = host.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Db>();
        db.Notes.Add(new Note { Text = "not replicated" });
        db.Logs.Add(new Log { Text = "replicated type, but not registered" });
        db.Items.Add(new Item { Code = "a", Region = "north" });
        await db.SaveChangesAsync(Ct);
        await host.Log.AllFinishedAsync();

        Assert.Equal(["Item 1"], host.Log.Started);
        Assert.Equal(
        [
            """upsert items/1 {"id":"1","Region":"north","ItemType":"Item","Rank":0,"Code":"a","CategoryID":null,"CategoryName":null,"Category":null}""",
        ], host.Cosmos.WriteLines);
    }
}
