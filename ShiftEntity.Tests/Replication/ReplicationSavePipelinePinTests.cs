using EntityFrameworkCore.Triggered;
using EntityFrameworkCore.Triggered.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.CosmosDbReplication;
using ShiftSoftware.ShiftEntity.CosmosDbReplication.Services;
using Xunit;
using static ShiftSoftware.ShiftEntity.Tests.Replication.PinModel;

namespace ShiftSoftware.ShiftEntity.Tests.Replication;

/// <summary>
/// Pins how replication sits in the save: when a sync starts relative to an explicit transaction, which save methods
/// replicate, host triggers that keep firing around it and that save again from inside their own after-save step, what
/// a refused write leaves on the row, and that replication never changes an audit column.
/// </summary>
/// <remarks>
/// Two pins were corrected when replication moved off the trigger library, each named in its comment: C1 (a sync
/// started before the commit of an explicit transaction, so a rolled-back save still reached Cosmos DB; now it starts
/// after the commit) and C2 (<c>SaveChangesWithoutTriggersAsync</c> skipped replication; now it replicates, and
/// <c>SuppressReplication()</c> is the way to skip it).
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

    // C1. Before the correction the sync started during SaveChanges, before the commit, and a rollback did not stop it:
    // the document of a row that did not exist was written.
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

    // C1. Before the correction the sync started during SaveChanges, before the commit.
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

    // C2. Before the correction SaveChangesWithoutTriggersAsync started no sync and left the row dirty.
    [Fact]
    public async Task SaveChangesWithoutTriggersAsync_replicates_like_any_save()
    {
        await using var host = ItemHost();
        await using var scope = host.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Db>();

        db.Items.Add(new Item { Code = "a", Region = "north" });
        await db.SaveChangesWithoutTriggersAsync(Ct);
        await host.Log.AllFinishedAsync();

        Assert.Equal(["Item 1"], host.Log.Started);
        Assert.Equal([NorthA], host.Cosmos.WriteLines);
        var stored = await host.StoredAsync<Item>(1);
        Assert.Equal((DateTimeOffset?)stored.LastSaveDate, stored.LastReplicationDate);
    }

    // C2: what a save that must not replicate uses now.
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

    // A host trigger that numbers new items after the insert, with a second save on the same context. It mirrors a
    // platform module's after-save trigger that numbers new claims, which hosts register through DI like this.
    public sealed class NumberingTrigger(Db db) : IAfterSaveTrigger<Item>
    {
        public async Task AfterSave(ITriggerContext<Item> context, CancellationToken cancellationToken)
        {
            if (context.ChangeType != ChangeType.Added)
                return;

            context.Entity.Rank = await db.Items.Where(x => x.ID != context.Entity.ID).CountAsync(cancellationToken) + 1;
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task A_row_that_a_host_trigger_saves_again_from_inside_its_after_save_step_is_replicated_with_that_change()
    {
        await using var host = ItemHost(services => services.AddTransient<IAfterSaveTrigger<Item>, NumberingTrigger>());
        await using var scope = host.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Db>();

        db.Items.Add(new Item { Code = "a", Region = "north" });
        await db.SaveChangesAsync(Ct);
        await host.Log.AllFinishedAsync();

        var stored = await host.StoredAsync<Item>(1);
        Assert.Equal(1, stored.Rank);
        Assert.Equal(
        [
            """upsert items/1 {"id":"1","Region":"north","ItemType":"Item","Rank":1,"Code":"a","CategoryID":null,"CategoryName":null,"Category":null}""",
            """upsert items/1 {"id":"1","Region":"north","ItemType":"Item","Rank":1,"Code":"a","CategoryID":null,"CategoryName":null,"Category":null}""",
        ], host.Cosmos.WriteLines);
        Assert.Equal(["Item 1", "Item 1"], host.Log.Started);
        Assert.Empty(host.Log.Failed);
    }

    // Host triggers in the three ways hosts register them, recording what they see.
    public sealed class HostTriggerCalls
    {
        public List<string> Calls { get; } = new();
        public void Add(string call) { lock (Calls) Calls.Add(call); }
    }

    public sealed class OptionsBeforeSave(HostTriggerCalls calls) : IBeforeSaveTrigger<Item>
    {
        public Task BeforeSave(ITriggerContext<Item> context, CancellationToken cancellationToken)
        {
            calls.Add($"options before {context.ChangeType} {context.Entity.Code}");
            context.Entity.Code = context.Entity.Code.Trim();
            return Task.CompletedTask;
        }
    }

    public sealed class ServiceAfterSave(HostTriggerCalls calls) : IAfterSaveTrigger<Item>
    {
        public Task AfterSave(ITriggerContext<Item> context, CancellationToken cancellationToken)
        {
            calls.Add($"service after {context.ChangeType} {context.Entity.Code}");
            return Task.CompletedTask;
        }
    }

    public sealed class OpenBeforeSave<T>(HostTriggerCalls calls) : IBeforeSaveTrigger<T> where T : class
    {
        public Task BeforeSave(ITriggerContext<T> context, CancellationToken cancellationToken)
        {
            calls.Add($"open before {context.ChangeType} {typeof(T).Name}");
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Host_triggers_keep_firing_whichever_way_the_host_registers_them()
    {
        var calls = new HostTriggerCalls();
        await using var host = ItemHost(
            services => services
                .AddSingleton(calls)
                .AddTransient<IAfterSaveTrigger<Item>, ServiceAfterSave>()
                .AddTransient(typeof(IBeforeSaveTrigger<>), typeof(OpenBeforeSave<>)),
            context => context.UseTriggers(x => x.AddTrigger<OptionsBeforeSave>()));
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

        // The library raises an open generic trigger once for each type the entity is: its class, every base class and
        // every interface. A trigger that should run for one type has to constrain itself to it.
        string[] ItemTypes(string change) =>
        [
            $"open before {change} Object",
            $"open before {change} ShiftEntityBase",
            $"open before {change} ShiftEntityBase`1",
            $"open before {change} IShiftEntityAudit",
            $"open before {change} ShiftEntity`1",
            $"open before {change} IShiftEntityReplication",
            $"open before {change} Item",
        ];

        Assert.Equal(
        [
            .. ItemTypes("Added"),
            "options before Added  a ",
            "open before Added Object",
            "open before Added ShiftEntityBase",
            "open before Added ShiftEntityBase`1",
            "open before Added IShiftEntityAudit",
            "open before Added ShiftEntity`1",
            "open before Added Note",
            "service after Added a",
            .. ItemTypes("Modified"),
            "options before Modified  b ",
            "service after Modified b",
        ], calls.Calls, StringComparer.Ordinal);

        // The before-save trigger's change reached the document.
        Assert.Equal(
        [
            NorthA,
            """upsert items/1 {"id":"1","Region":"north","ItemType":"Item","Rank":0,"Code":"b","CategoryID":null,"CategoryName":null,"Category":null}""",
        ], host.Cosmos.WriteLines);
    }

    [Fact]
    public async Task Host_triggers_registered_through_DI_fire_even_when_only_the_replication_registration_switches_the_triggers_on()
    {
        var calls = new HostTriggerCalls();
        await using var host = ItemHost(services => services
            .AddSingleton(calls)
            .AddTransient<IAfterSaveTrigger<Item>, ServiceAfterSave>());
        await using var scope = host.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Db>();

        db.Items.Add(new Item { Code = "a", Region = "north" });
        await db.SaveChangesAsync(Ct);
        await host.Log.AllFinishedAsync();

        Assert.Equal(["service after Added a"], calls.Calls);
    }

    [Fact]
    public async Task Neither_path_changes_an_audit_column()
    {
        var saved = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
        var created = new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);

        await using var host = ItemHost(services => services.AddShiftEntityCosmosDbReplication<Db>());
        await using var scope = host.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Db>();

        // The trigger path. The values are set by hand, so the context's insert backfill keeps them.
        db.Items.Add(new Item
        {
            Code = "a", Region = "north", CreateDate = created, CreatedByUserID = 5, LastSaveDate = saved, LastSavedByUserID = 6,
        });
        await db.SaveChangesAsync(Ct);
        await host.Log.AllFinishedAsync();

        // The catch-up path, on a dirty row written without triggers.
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
