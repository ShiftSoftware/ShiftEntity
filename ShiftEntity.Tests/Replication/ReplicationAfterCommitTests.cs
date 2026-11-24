using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.CosmosDbReplication;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.Model.Replication;
using Xunit;
using static ShiftSoftware.ShiftEntity.Tests.Replication.PinModel;

namespace ShiftSoftware.ShiftEntity.Tests.Replication;

/// <summary>
/// Pins the edges of the work that waits for the commit: which transaction's commit runs it, a pooled context's later
/// leases, a context that joined another's transaction and so cannot hold such work, a save whose transaction cannot
/// begin, one row saved more than once in a transaction, and which rows a save reports.
/// </summary>
public class ReplicationAfterCommitTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ReplicationPinHost ItemHost(Action<IServiceCollection>? services = null) => new(
        (o, client) => o.SetUpReplication<Db, Item>(client, ReplicationPinHost.Database)
            .Replicate<ItemDocument>("items", x => x.id, x => Document(x.Entity)),
        services, null, ("items", ["/id"]));

    // A transaction left by an exception is disposed without a commit or a rollback, which tells the context
    // nothing. Its work used to stay queued and run at the context's next commit, writing the document of a row that
    // was rolled back.
    [Fact]
    public async Task The_work_of_a_transaction_disposed_without_a_rollback_never_runs_at_a_later_commit()
    {
        await using var host = ItemHost();
        await using var scope = host.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Db>();

        await SaveAndFailAsync(db, "ghost");
        db.ChangeTracker.Clear();

        // A later unit of work on the same context: an unrelated row, then a real item that gets the ghost's key.
        await using (var transaction = await db.Database.BeginTransactionAsync(Ct))
        {
            db.Notes.Add(new Note { Text = "unrelated" });
            await db.SaveChangesAsync(Ct);
            await transaction.CommitAsync(Ct);
        }
        await host.Log.AllFinishedAsync();
        Assert.Empty(host.Log.Started);

        await using (var transaction = await db.Database.BeginTransactionAsync(Ct))
        {
            db.Items.Add(new Item { Code = "real", Region = "north" });
            await db.SaveChangesAsync(Ct);
            await transaction.CommitAsync(Ct);
        }
        await host.Log.AllFinishedAsync();

        Assert.Equal(["Item 1"], host.Log.Started);
        Assert.Equal(["""{"id":"1","Region":"north","ItemType":"Item","Rank":0,"Code":"real","CategoryID":null,"CategoryName":null,"Category":null}"""],
            host.Cosmos.StoredLines("items"));
        var stored = await host.StoredAsync<Item>(1);
        Assert.Equal((DateTimeOffset?)stored.LastSaveDate, stored.LastReplicationDate);
    }

    private static async Task SaveAndFailAsync(Db db, string code)
    {
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            db.Items.Add(new Item { Code = code, Region = "north" });
            await db.SaveChangesAsync(Ct);
            throw new InvalidOperationException("the next step failed");
        }
        catch (InvalidOperationException)
        {
        }
    }

    // A pooled context's next lease starts without the work of the last one.
    [Fact]
    public async Task A_pooled_contexts_next_lease_does_not_run_the_work_of_the_last_one()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var cosmos = new RecordingCosmos(ReplicationPinHost.Database).Container("items", "/id");
        var log = new ReplicationSyncLog();
        var services = new ServiceCollection();
        services.AddLogging(x => x.AddProvider(log));
        services.AddPooledDbContextFactory<Db>(o => o.UseSqlite(connection));
        services.AddScoped(x => x.GetRequiredService<IDbContextFactory<Db>>().CreateDbContext());
        services.AddShiftEntityCosmosDbReplicationOnSave<Db>(o => o.SetUpReplication<Db, Item>(cosmos.Client, ReplicationPinHost.Database)
            .Replicate<ItemDocument>("items", x => x.id, x => Document(x.Entity)));
        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<Db>>();

        await using (var db = await factory.CreateDbContextAsync(Ct))
        {
            await db.Database.EnsureCreatedAsync(Ct);
            await SaveAndFailAsync(db, "ghost");
            _ = db.SuppressReplication();       // a scope the lease never disposed
        }

        await using (var db = await factory.CreateDbContextAsync(Ct))
        {
            Assert.True(db.ContextId.Lease > 1);
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            db.Items.Add(new Item { Code = "real", Region = "north" });
            await db.SaveChangesAsync(Ct);
            await transaction.CommitAsync(Ct);
        }
        await log.AllFinishedAsync();

        Assert.Equal(["Item 1"], log.Started);
        Assert.Equal(["upsert items/1"], cosmos.Writes.Select(x => $"{x.Kind} {x.Container}/{x.Id}"));
        Assert.Contains("\"Code\":\"real\"", cosmos.StoredLines("items").Single());
    }

    // EF clears a pooled context's change tracker handlers when it goes back to the pool. The context used to
    // watch its change tracker once per instance, so later leases reported no saves: nothing replicated and, for a
    // replication tree, nothing was marked either.
    [Fact]
    public async Task A_pooled_context_replicates_and_marks_on_every_lease()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var cosmos = new RecordingCosmos(ReplicationPinHost.Database).Container("shops", "/id");
        var log = new ReplicationSyncLog();
        var services = new ServiceCollection();
        services.AddLogging(x => x.AddProvider(log));
        services.AddPooledDbContextFactory<TreeModel.Db>(o => o.UseSqlite(connection));
        services.AddScoped(x => x.GetRequiredService<IDbContextFactory<TreeModel.Db>>().CreateDbContext());
        services.AddShiftEntityCosmosDbReplicationOnSave<TreeModel.Db>(o =>
            o.SetUpReplication<TreeModel.Db, TreeModel.Shop>(cosmos.Client, ReplicationPinHost.Database)
                .ReplicateTree<TreeModel.ShopDocument>("shops", x => x.id, x => TreeModel.Document(x.Entity)));
        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<TreeModel.Db>>();

        await using (var db = await factory.CreateDbContextAsync(Ct))
        {
            await db.Database.EnsureCreatedAsync(Ct);
            using (db.SuppressReplication())
            {
                db.Shops.AddRange(
                    new TreeModel.Shop { Name = "Corner", Owner = new TreeModel.Owner { Name = "Ada" } },
                    new TreeModel.Shop { Name = "Dock", Owner = new TreeModel.Owner { Name = "Bo" } });
                await db.SaveChangesAsync(Ct);
            }
            await db.Shops.ExecuteUpdateAsync(s => s.SetProperty(x => x.LastReplicationDate, x => (DateTimeOffset?)x.LastSaveDate), Ct);
        }

        var leases = new List<int>();
        foreach (var (owner, name) in new[] { (1L, "Ana"), (2L, "Bea"), (1L, "Abe") })
        {
            await using (var db = await factory.CreateDbContextAsync(Ct))
            {
                leases.Add(db.ContextId.Lease);
                (await db.Owners.SingleAsync(x => x.ID == owner, Ct)).Name = name;
                await db.SaveChangesAsync(Ct);
            }
            await log.AllFinishedAsync();
        }

        Assert.Contains(leases, x => x > 1);
        Assert.Equal(["Shop 1", "Shop 2", "Shop 1"], log.Started);
        log.AssertNoneFailed();
        Assert.Equal(
        [
            """{"id":"1","Name":"Corner","Town":null,"District":null,"Land":null,"Owner":"Abe","Products":[],"Days":[]}""",
            """{"id":"2","Name":"Dock","Town":null,"District":null,"Land":null,"Owner":"Bea","Products":[],"Days":[]}""",
        ], cosmos.StoredLines("shops"));
    }

    // The two saves' syncs used to start together at the commit: the delete found no document yet, then the add
    // wrote one, for a row that does not exist.
    [Fact]
    public async Task A_row_added_and_deleted_in_one_transaction_writes_nothing()
    {
        await using var host = ItemHost();
        await using (var scope = host.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Db>();
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            var item = new Item { Code = "a", Region = "north" };
            db.Items.Add(item);
            await db.SaveChangesAsync(Ct);
            db.Items.Remove(item);
            await db.SaveChangesAsync(Ct);
            await transaction.CommitAsync(Ct);
        }
        await host.Log.AllFinishedAsync();

        Assert.False(await host.ExistsAsync<Item>(1));
        Assert.Empty(host.Log.Started);
        Assert.Empty(host.Cosmos.Writes);
    }

    // The delete and the add of one key used to start together at the commit, so a slow delete could remove the
    // new document after it was written, and the row then looked replicated with no document.
    [Fact]
    public async Task A_row_deleted_and_added_again_in_one_transaction_is_deleted_and_then_written_in_that_order()
    {
        await using var host = ItemHost();
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

        await using (var scope = host.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Db>();
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            db.Items.RemoveRange(await db.Items.ToListAsync(Ct));
            await db.SaveChangesAsync(Ct);
            db.Items.Add(new Item { ID = 1, Code = "b", Region = "north" });
            await db.SaveChangesAsync(Ct);
            await transaction.CommitAsync(Ct);
        }
        await host.Log.AllFinishedAsync();

        Assert.Equal(["delete items/1", "upsert items/1"], host.Cosmos.Writes.Skip(1).Select(x => $"{x.Kind} {x.Container}/{x.Id}"));
        Assert.Contains("\"Code\":\"b\"", host.Cosmos.StoredLines("items").Single());
        var stored = await host.StoredAsync<Item>(1);
        Assert.Equal((DateTimeOffset?)stored.LastSaveDate, stored.LastReplicationDate);
    }

    // EF tells only the context that began a transaction about its commit. A context that joined it with
    // UseTransaction used to queue its syncs for a commit it never heard of, and later started them at once, before
    // the commit. The context cannot follow such a transaction, so a save of rows it replicates is refused there
    // before it writes anything. Under SuppressReplication the save goes through, for a catch-up to replicate.
    [Fact]
    public async Task A_context_that_joined_another_contexts_transaction_refuses_to_save_rows_it_replicates()
    {
        await using var host = ItemHost();
        await using var owner = host.Provider.CreateAsyncScope();
        await using var joining = host.Provider.CreateAsyncScope();
        var a = owner.ServiceProvider.GetRequiredService<Db>();
        var b = joining.ServiceProvider.GetRequiredService<Db>();

        await using (var transaction = await a.Database.BeginTransactionAsync(Ct))
        {
            // A transaction can be joined only on its own connection, and each context has a connection of its own.
            b.Database.SetDbConnection(a.Database.GetDbConnection(), contextOwnsConnection: false);
            await b.Database.UseTransactionAsync(transaction.GetDbTransaction(), Ct);
            var item = new Item { Code = "joined", Region = "north" };
            b.Items.Add(item);

            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => b.SaveChangesAsync(Ct));
            Assert.Contains("cannot save Item rows, for which replication waits for the commit", refused.Message);
            Assert.Contains("UseTransaction", refused.Message);
            Assert.Contains("SuppressReplication()", refused.Message);
            Assert.Equal(EntityState.Added, b.Entry(item).State);

            // A row that is not replicated has nothing that waits, and is saved in the joined transaction.
            b.Entry(item).State = EntityState.Detached;
            b.Notes.Add(new Note { Text = "not replicated" });
            await b.SaveChangesAsync(Ct);

            b.Items.Add(item);
            using (b.SuppressReplication())
                await b.SaveChangesAsync(Ct);

            await transaction.CommitAsync(Ct);
        }
        await b.Database.UseTransactionAsync(null, Ct);
        await host.Log.AllFinishedAsync();

        Assert.Empty(host.Log.Started);
        Assert.Empty(host.Cosmos.Writes);
        Assert.True(await host.ExistsAsync<Item>(1));
        Assert.True(await host.ExistsAsync<Note>(1));
    }

    // A sync that loads a tree, started in a joined transaction, read the tree on its own connection before the
    // commit. Under snapshot reads that is the tree before the change, and its watermark then cleared the mark the save
    // made, so the root looked replicated with the old tree. A save that changes a replicated tree is refused there
    // too. Under SuppressReplication it marks the roots, and the catch-up replicates them once the transaction has
    // committed.
    [Fact]
    public async Task A_context_that_joined_another_contexts_transaction_refuses_a_tree_save_and_leaves_it_to_the_catch_up_when_replication_is_off()
    {
        await using var host = new ShopTreeHost();
        await host.SeedAsync();
        await host.CatchUpAsync(updateAll: true);
        var before = host.Cosmos.Writes.Count;

        await using var owner = host.Provider.CreateAsyncScope();
        await using var joining = host.Provider.CreateAsyncScope();
        var a = owner.ServiceProvider.GetRequiredService<TreeModel.Db>();
        var b = joining.ServiceProvider.GetRequiredService<TreeModel.Db>();

        await using (var transaction = await a.Database.BeginTransactionAsync(Ct))
        {
            // A transaction can be joined only on its own connection, and each context has a connection of its own.
            b.Database.SetDbConnection(a.Database.GetDbConnection(), contextOwnsConnection: false);
            await b.Database.UseTransactionAsync(transaction.GetDbTransaction(), Ct);

            // A part of shop 1 alone: no replicated row is written, and its root's tree document still waits.
            (await b.Owners.SingleAsync(x => x.ID == 1, Ct)).Name = "Ana";
            var part = await Assert.ThrowsAsync<InvalidOperationException>(() => b.SaveChangesAsync(Ct));
            Assert.Contains("cannot save Shop rows, for which replication waits for the commit", part.Message);

            (await b.Shops.SingleAsync(x => x.ID == 2, Ct)).Name = "Dock 2";     // shop 2 itself
            await Assert.ThrowsAsync<InvalidOperationException>(() => b.SaveChangesAsync(Ct));

            using (b.SuppressReplication())
                await b.SaveChangesAsync(Ct);

            await transaction.CommitAsync(Ct);
        }
        await b.Database.UseTransactionAsync(null, Ct);
        await host.Log.AllFinishedAsync();

        Assert.Empty(host.Log.Started);
        Assert.Empty(host.Cosmos.Writes.Skip(before));
        Assert.Equal("[1,2]", await host.MarkedAsync());

        var result = await host.CatchUpAsync();
        Assert.Equal((2, 2, 0), (result.Selected, result.Replicated, result.Failed));
        Assert.Equal(
        [
            """{"id":"1","Name":"Corner","Town":"Oak","District":"Hills","Land":"North","Owner":"Ana","Products":[],"Days":[]}""",
            """{"id":"2","Name":"Dock 2","Town":"Pier","District":"Coast","Land":"South","Owner":"Bo","Products":[],"Days":[]}""",
            """{"id":"3","Name":"Mill","Town":"Oak","District":"Hills","Land":"North","Owner":"Bo","Products":[],"Days":[]}""",
        ], host.Shops);
        Assert.Equal("[]", await host.MarkedAsync());
    }

    // The saves of one transaction combine one row's syncs: a row whose partition key moved twice loses its first
    // document and is written once under its last key, and a row that moved and was then deleted only loses its first
    // document.
    [Fact]
    public async Task Rows_moved_and_deleted_in_one_transaction_leave_only_their_last_state()
    {
        await using var host = new ReplicationPinHost(
            (o, client) => o.SetUpReplication<Db, Item>(client, ReplicationPinHost.Database)
                .Replicate<ItemDocument>("items", x => x.Region, x => Document(x.Entity)),
            null, null, ("items", ["/Region"]));

        await using (var scope = host.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Db>();
            db.Items.AddRange(new Item { Code = "a", Region = "north" }, new Item { Code = "b", Region = "north" });
            await db.SaveChangesAsync(Ct);
        }
        await host.Log.AllFinishedAsync();
        host.Log.AssertNoneFailed();
        var before = host.Cosmos.Writes.Count;

        await using (var scope = host.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Db>();
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            var a = await db.Items.SingleAsync(x => x.ID == 1, Ct);
            var b = await db.Items.SingleAsync(x => x.ID == 2, Ct);
            a.Region = "south";
            b.Region = "south";
            await db.SaveChangesAsync(Ct);
            a.Region = "east";
            db.Items.Remove(b);
            await db.SaveChangesAsync(Ct);
            await transaction.CommitAsync(Ct);
        }
        await host.Log.AllFinishedAsync();

        host.Log.AssertNoneFailed();
        var writes = host.Cosmos.Writes.Skip(before).Select(x => $"{x.Kind} {x.Id} {x.PartitionKey ?? (string?)x.Body?["Region"]}").ToList();
        Assert.Equal(["delete 1 [\"north\"]", "upsert 1 east"], writes.Where(x => x.Split(' ')[1] == "1"));
        Assert.Equal(["delete 2 [\"north\"]"], writes.Where(x => x.Split(' ')[1] == "2"));
        Assert.Contains("\"Region\":\"east\"", host.Cosmos.StoredLines("items").Single());
        var stored = await host.StoredAsync<Item>(1);
        Assert.Equal((DateTimeOffset?)stored.LastSaveDate, stored.LastReplicationDate);
        Assert.Equal("east", LastReplicationStamp.Deserialize(stored.LastReplicationStamp)?.Level1?.Value);
    }

    // Fails the next transaction to begin, once.
    public sealed class FailingBegin : DbTransactionInterceptor
    {
        public bool Armed { get; set; }

        public override ValueTask<InterceptionResult<System.Data.Common.DbTransaction>> TransactionStartingAsync(System.Data.Common.DbConnection connection,
            TransactionStartingEventData eventData, InterceptionResult<System.Data.Common.DbTransaction> result, CancellationToken cancellationToken = default)
        {
            if (!Armed)
                return ValueTask.FromResult(result);

            Armed = false;
            throw new TimeoutException("The transaction could not begin (simulated).");
        }
    }

    private static object? Field(ShiftDbContext db, string name) =>
        typeof(ShiftDbContext).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(db);

    // The save used to start collecting its rows before its transaction began, outside the block that stops it.
    // When the transaction could not begin, the list stayed, and every later save took it for an outer save's list.
    [Fact]
    public async Task A_save_whose_transaction_cannot_begin_leaves_nothing_behind_and_its_retry_marks_and_replicates()
    {
        var failingBegin = new FailingBegin();
        await using var host = new ShopTreeHost(context: o => o.AddInterceptors(failingBegin));
        await host.SeedAsync();

        await using (var scope = host.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TreeModel.Db>();
            (await db.Owners.SingleAsync(x => x.ID == 2, Ct)).Name = "Bea";

            failingBegin.Armed = true;
            await Assert.ThrowsAsync<TimeoutException>(() => db.SaveChangesAsync(Ct));
            Assert.Null(Field(db, "savedRows"));
            Assert.Equal(0, Field(db, "collectingSaves"));

            await db.SaveChangesAsync(Ct);
        }
        await host.Log.AllFinishedAsync();

        host.Log.AssertNoneFailed();
        Assert.Equal(["Shop 2", "Shop 3"], host.Log.Started.Order());
        Assert.All(host.Shops, x => Assert.Contains("\"Owner\":\"Bea\"", x));
    }

    // A replicated type with a type derived from it, mapped to the same table.
    public class Animal : ShiftEntity<Animal>, IShiftEntityReplication
    {
        public string Name { get; set; } = "";
        public DateTimeOffset? LastReplicationDate { get; set; }
        public string? LastReplicationStamp { get; set; }
    }

    public sealed class Dog : Animal
    {
        public string Breed { get; set; } = "";
    }

    public sealed class ZooDb(DbContextOptions<ZooDb> options) : ShiftDbContext(options)
    {
        public DbSet<Animal> Animals => Set<Animal>();
        public DbSet<Dog> Dogs => Set<Dog>();
    }

    public sealed class AnimalDocument
    {
        public string id { get; set; } = "";
        public string Name { get; set; } = "";
    }

    // The trigger library raised the replication trigger for every type a row is, so a derived row replicated
    // through its base type's registration. The observer used to look for a registration of the row's own type only.
    [Fact]
    public async Task A_row_of_a_type_derived_from_a_replicated_type_replicates_through_its_registration()
    {
        // A file that every context opens a connection of its own to: the save's two syncs run at once.
        await using var database = new SqliteTestFile();
        using var cosmos = new RecordingCosmos(ReplicationPinHost.Database).Container("animals", "/id");
        var log = new ReplicationSyncLog();
        var services = new ServiceCollection();
        services.AddLogging(x => x.AddProvider(log));
        services.AddDbContext<ZooDb>(o => database.Use(o));
        services.AddShiftEntityCosmosDbReplicationOnSave<ZooDb>(o => o.SetUpReplication<ZooDb, Animal>(cosmos.Client, ReplicationPinHost.Database)
            .Replicate<AnimalDocument>("animals", x => x.id, x => new AnimalDocument { id = x.Entity.ID.ToString(), Name = x.Entity.Name }));
        await using var provider = services.BuildServiceProvider(validateScopes: true);

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ZooDb>();
            await db.Database.EnsureCreatedAsync(Ct);
            db.Animals.AddRange(new Animal { Name = "cat" }, new Dog { Name = "rex", Breed = "lab" });
            await db.SaveChangesAsync(Ct);
        }
        await log.AllFinishedAsync();

        log.AssertNoneFailed();
        Assert.Equal(["Animal 1", "Dog 2"], log.Started.Order());
        Assert.Equal(["""{"id":"1","Name":"cat"}""", """{"id":"2","Name":"rex"}"""], cosmos.StoredLines("animals"));

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ZooDb>();
            var stored = await db.Dogs.AsNoTracking().SingleAsync(Ct);
            Assert.Equal((DateTimeOffset?)stored.LastSaveDate, stored.LastReplicationDate);
        }
    }

    // Stops tracking a new item before it is written, as a host's before-save handler might.
    public sealed class DropBeforeSave : IBeforeSaveHandler<Item>
    {
        public ValueTask BeforeSaveAsync(PendingRows<Item> rows, CancellationToken cancellationToken)
        {
            foreach (var row in rows)
                if (row.Entity.Code == "drop")
                    rows.Entry(row).State = EntityState.Detached;

            return ValueTask.CompletedTask;
        }
    }

    // Every change to Unchanged or Detached during the save used to count as a write, so a new row a before-save
    // step detached was reported as added, and replicated as a document with id 0 for a row that was never written.
    [Fact]
    public async Task A_new_row_that_a_before_save_handler_stops_tracking_is_not_reported()
    {
        await using var host = ItemHost(services => services.AddSaveHandler<DropBeforeSave>());
        await using (var scope = host.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Db>();
            db.Items.AddRange(new Item { Code = "keep", Region = "north" }, new Item { Code = "drop", Region = "north" });
            await db.SaveChangesAsync(Ct);
        }
        await host.Log.AllFinishedAsync();

        Assert.Equal(["Item 1"], host.Log.Started);
        Assert.Equal(["upsert items/1"], host.Cosmos.Writes.Select(x => $"{x.Kind} {x.Container}/{x.Id}"));
    }

    // The saves of one transaction start their syncs as one batch at its commit. A save that the app undoes with a
    // rollback to a savepoint of its own is not in that batch: its rows were never committed. The context used to keep
    // one growing batch for the transaction, which a savepoint could not cut.
    [Fact]
    public async Task A_rollback_to_a_savepoint_takes_the_syncs_of_the_saves_after_it_out_of_the_commit()
    {
        await using var host = ItemHost();
        await using (var scope = host.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Db>();
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);

            db.Items.Add(new Item { Code = "kept", Region = "north" });
            await db.SaveChangesAsync(Ct);

            await transaction.CreateSavepointAsync("before the second", Ct);
            db.Items.Add(new Item { Code = "undone", Region = "north" });
            await db.SaveChangesAsync(Ct);
            await transaction.RollbackToSavepointAsync("before the second", Ct);
            db.ChangeTracker.Clear();

            db.Items.Add(new Item { Code = "after the rollback", Region = "north" });
            await db.SaveChangesAsync(Ct);

            Assert.Empty(host.Log.Started);
            await transaction.CommitAsync(Ct);
        }
        await host.Log.AllFinishedAsync();

        host.Log.AssertNoneFailed();
        var stored = host.Cosmos.StoredLines("items");
        Assert.Equal(2, stored.Count);
        Assert.Contains(stored, x => x.Contains("\"Code\":\"kept\""));
        Assert.Contains(stored, x => x.Contains("\"Code\":\"after the rollback\""));
        Assert.DoesNotContain(stored, x => x.Contains("undone"));
        Assert.Equal(2, host.Log.Started.Count);
    }
}
