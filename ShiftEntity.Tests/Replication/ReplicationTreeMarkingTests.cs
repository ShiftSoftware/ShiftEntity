using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.CosmosDbReplication.Services;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.EFCore.Replication;
using ShiftSoftware.ShiftEntity.Model.Replication;
using Xunit;
using static ShiftSoftware.ShiftEntity.Tests.Replication.TreeModel;

namespace ShiftSoftware.ShiftEntity.Tests.Replication;

/// <summary>
/// Pins when a save marks the roots of a replication tree as not replicated: whenever the context declares the tree,
/// whether or not anything replicates the save now, except for a change of nothing but the replication columns. Also
/// what such a save costs and refuses: one change detection, one transaction of its own without a savepoint, no
/// retrying execution strategy, and a bounded number of tree syncs at once.
/// </summary>
public class ReplicationTreeMarkingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task RenameOwnerAsync(ShopTreeHost host, long id, string name, Func<Db, IDisposable>? scope = null,
        bool accept = true)
    {
        await using var services = host.Provider.CreateAsyncScope();
        var db = services.ServiceProvider.GetRequiredService<Db>();
        (await db.Owners.SingleAsync(x => x.ID == id, Ct)).Name = name;

        using (scope?.Invoke(db))
            await db.SaveChangesAsync(accept, Ct);
    }

    // RV-13. The scope suppresses only the syncs. Before, the save marked nothing, so the dirty-only catch-up never
    // rebuilt the shops, although SuppressReplication's doc promised that a later catch-up would.
    [Fact]
    public async Task A_part_saved_under_SuppressReplication_marks_its_roots_and_the_catch_up_rebuilds_them()
    {
        await using var host = new ShopTreeHost();
        await host.SeedAsync();

        await RenameOwnerAsync(host, 2, "Bea", db => db.SuppressReplication());
        await host.Log.AllFinishedAsync();

        Assert.Empty(host.Log.Started);
        Assert.Equal("[2,3]", await host.MarkedAsync());

        var result = await host.CatchUpAsync();

        Assert.Equal((2, 2, 0), (result.Selected, result.Replicated, result.Failed));
        Assert.Equal(2, host.Shops.Count);
        Assert.All(host.Shops, x => Assert.Contains("\"Owner\":\"Bea\"", x));
        Assert.Equal("[]", await host.MarkedAsync());
    }

    // RV-13. A process that replicates nothing, such as a tool or a Functions app without the registration.
    [Fact]
    public async Task A_part_saved_in_a_process_without_replication_marks_its_roots()
    {
        await using var host = new ShopTreeHost(replication: false);
        await host.SeedAsync();

        await RenameOwnerAsync(host, 1, "Abe");

        Assert.Equal("[1]", await host.MarkedAsync());
        Assert.Equal(1, (await host.CatchUpAsync()).Replicated);
        Assert.Contains("\"Owner\":\"Abe\"", host.Shops.Single());
    }

    // RV-13. A save that does not accept its changes reports nothing, so nothing replicates now, but it marks.
    [Fact]
    public async Task A_part_saved_without_accepting_its_changes_marks_its_roots()
    {
        await using var host = new ShopTreeHost();
        await host.SeedAsync();

        await RenameOwnerAsync(host, 2, "Bea", accept: false);
        await host.Log.AllFinishedAsync();

        Assert.Empty(host.Log.Started);
        Assert.Equal("[2,3]", await host.MarkedAsync());
    }

    // A sync records what it wrote in the row's two replication columns. A save of nothing else, of a part or of the
    // root, changes no document, and marking for it would undo the record or make every part sync dirty its roots.
    [Fact]
    public async Task A_save_of_nothing_but_the_replication_columns_marks_no_root()
    {
        await using var host = new ShopTreeHost();
        await host.SeedAsync();
        host.Cosmos.Refuse = _ => true;     // the syncs record nothing, so a mark would stay visible
        var shop2 = await host.StoredAsync(2);

        await using (var scope = host.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Db>();
            var owner = await db.Owners.SingleAsync(x => x.ID == 1, Ct);
            owner.LastReplicationDate = owner.LastSaveDate;
            owner.LastReplicationStamp = """{"ID":"1"}""";
            (await db.Shops.SingleAsync(x => x.ID == 2, Ct)).LastReplicationStamp = """{"ID":"2"}""";
            await db.SaveChangesAsync(Ct);
        }
        await host.Log.AllFinishedAsync();

        Assert.Equal("[]", await host.MarkedAsync());
        Assert.Equal(shop2.LastReplicationDate, (await host.StoredAsync(2)).LastReplicationDate);

        // A real change of the same part does mark.
        await RenameOwnerAsync(host, 1, "Ana");
        await host.Log.AllFinishedAsync();
        Assert.Equal("[1]", await host.MarkedAsync());
    }

    // RV-17. The context's own transaction for the save: BEGIN, the part's UPDATE, the query for its roots, the mark, and
    // COMMIT. EF's automatic savepoint used to come first, which nothing rolls back to.
    [Fact]
    public async Task A_part_save_runs_in_a_transaction_of_its_own_with_no_savepoint()
    {
        await using var host = new ShopTreeHost();
        await host.SeedAsync();

        Db? saving = null;
        await host.SaveAsync(async db =>
        {
            saving = db;
            (await db.Owners.SingleAsync(x => x.ID == 2, Ct)).Name = "Bea";
            host.Sql.Clear();
        });

        var sent = host.Sql.SentBy(saving!);
        Assert.Equal(5, sent.Count);
        Assert.Equal("BEGIN", sent[0]);
        Assert.StartsWith("UPDATE \"Owners\"", sent[1]);
        Assert.StartsWith("SELECT", sent[2]);
        Assert.StartsWith("UPDATE \"Shops\"", sent[3]);
        Assert.Contains("\"LastReplicationDate\" IS NOT NULL", sent[3]);   // RV-04: only roots not marked already
        Assert.Equal("COMMIT", sent[4]);
    }

    public abstract class CountingDb(DbContextOptions options) : ShiftDbContext(options)
    {
        public DbSet<Shop> Shops => Set<Shop>();
        public DbSet<Owner> Owners => Set<Owner>();
        public DbSet<Unrelated> Unrelated => Set<Unrelated>();
    }

    public sealed class CountingTreeDb(DbContextOptions<CountingTreeDb> options) : CountingDb(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Shop>().HasReplicationTree(q => q.Include(x => x.Owner));
        }
    }

    public sealed class CountingPlainDb(DbContextOptions<CountingPlainDb> options) : CountingDb(options);

    private static async Task<int> ChangeDetectionsAsync<TDb>(Func<TDb, Task> change) where TDb : CountingDb
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<TDb>(o => o.UseSqlite(connection));
        services.AddShiftEntityCosmosDbReplicationTrigger<TDb>(_ => { });
        await using var provider = services.BuildServiceProvider(validateScopes: true);
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TDb>();
        await db.Database.EnsureCreatedAsync(Ct);
        db.Add(new Owner { Name = "Ada" });
        await db.SaveChangesAsync(Ct);

        await change(db);
        var passes = 0;
        db.ChangeTracker.DetectingAllChanges += (_, _) => passes++;
        await db.SaveChangesAsync(Ct);
        return passes;
    }

    // RV-17. The tree's look at the changed rows reuses the change detection the audit stamping has just run; it used to
    // run one more over every tracked row, on every save of a context with trees.
    [Fact]
    public async Task A_context_with_trees_detects_changes_as_often_as_one_without()
    {
        Task AddUnrelated(CountingDb db) { db.Unrelated.Add(new Unrelated { Text = "x" }); return Task.CompletedTask; }
        async Task RenameOwner(CountingDb db) => (await db.Owners.SingleAsync(Ct)).Name = "Ana";

        Assert.Equal(await ChangeDetectionsAsync<CountingPlainDb>(AddUnrelated), await ChangeDetectionsAsync<CountingTreeDb>(AddUnrelated));
        Assert.Equal(await ChangeDetectionsAsync<CountingPlainDb>(RenameOwner), await ChangeDetectionsAsync<CountingTreeDb>(RenameOwner));
    }

    // Retries a timeout, as EnableRetryOnFailure does a transient SQL error.
    public sealed class RetryingStrategy(ExecutionStrategyDependencies dependencies)
        : ExecutionStrategy(dependencies, 3, TimeSpan.FromMilliseconds(5))
    {
        protected override bool ShouldRetryOn(Exception exception) => exception is TimeoutException;
    }

    // RV-03. EF accepts a tree save's changes before the mark and the commit, so a retried attempt would save nothing
    // and lose the changes without an error. Such a save is refused at once instead, also inside the strategy, where it
    // used to run.
    [Fact]
    public async Task A_tree_save_under_a_retrying_execution_strategy_is_refused_before_it_writes_anything()
    {
        await using var host = new ShopTreeHost(sqlite: s => s.ExecutionStrategy(x => new RetryingStrategy(x)));
        await host.SeedAsync();

        await using (var scope = host.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Db>();
            var owner = await db.Owners.SingleAsync(x => x.ID == 2, Ct);
            owner.Name = "Bea";

            var outside = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
            Assert.Contains("retries on failure (RetryingStrategy)", outside.Message);

            var inside = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                db.Database.CreateExecutionStrategy().ExecuteAsync(() => db.SaveChangesAsync(Ct)));
            Assert.Contains("retries on failure", inside.Message);
            Assert.Equal(EntityState.Modified, db.Entry(owner).State);
        }

        await using (var db = host.Bare())
            Assert.Equal("Bo", (await db.Owners.SingleAsync(x => x.ID == 2, Ct)).Name);
        Assert.Equal("[]", await host.MarkedAsync());

        // Saves whose tree work needs no SQL are not refused: a row in no tree, and a new root with its links.
        await host.SaveAsync(db =>
        {
            db.Unrelated.Add(new Unrelated { Text = "x" });
            var shop = new Shop { Name = "Kiosk", TownID = 1, OwnerID = 1 };
            shop.Products.Add(new ShopProduct { ProductID = 1 });
            db.Shops.Add(shop);
            return Task.CompletedTask;
        });
        Assert.Contains(host.Shops, x => x.Contains("\"Name\":\"Kiosk\""));
    }

    // A tree whose part has a subtype mapped to the same table.
    public class Vendor : ShiftEntity<Vendor>
    {
        public string Name { get; set; } = "";
    }

    public sealed class LocalVendor : Vendor
    {
        public string Town { get; set; } = "";
    }

    public sealed class Kiosk : ShiftEntity<Kiosk>, IShiftEntityReplication
    {
        public long? VendorID { get; set; }
        public Vendor? Vendor { get; set; }
        public DateTimeOffset? LastReplicationDate { get; set; }
        public string? LastReplicationStamp { get; set; }
    }

    public sealed class VendorDb(DbContextOptions<VendorDb> options) : ShiftDbContext(options)
    {
        public DbSet<Vendor> Vendors => Set<Vendor>();
        public DbSet<Kiosk> Kiosks => Set<Kiosk>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<LocalVendor>();
            modelBuilder.Entity<Kiosk>().HasReplicationTree(q => q.Include(x => x.Vendor));
        }
    }

    // RV-11, for trees. A row of a derived type sits in the tree where its base type does; it used to be looked up by
    // its own type, found in no tree, and marked nothing.
    [Fact]
    public async Task A_part_of_a_derived_type_marks_its_roots()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        VendorDb Context() => new(new DbContextOptionsBuilder<VendorDb>().UseSqlite(connection).Options);

        await using (var db = Context())
        {
            await db.Database.EnsureCreatedAsync(Ct);
            db.Kiosks.AddRange(new Kiosk { Vendor = new LocalVendor { Name = "Ada", Town = "Oak" } }, new Kiosk { Vendor = new Vendor { Name = "Bo" } });
            await db.SaveChangesAsync(Ct);
            await db.Kiosks.ExecuteUpdateAsync(s => s.SetProperty(x => x.LastReplicationDate, x => (DateTimeOffset?)x.LastSaveDate), Ct);
        }

        await using (var db = Context())
        {
            (await db.Vendors.OfType<LocalVendor>().SingleAsync(Ct)).Town = "Pier";
            await db.SaveChangesAsync(Ct);
        }

        await using (var db = Context())
            Assert.Equal([1L], await db.Kiosks.Where(x => x.LastReplicationDate == null).Select(x => x.ID).ToListAsync(Ct));
    }

    // RV-18. A part shared by many roots starts a tree sync for each, and each takes a connection, reads the container
    // and writes; they used to start all at once.
    [Fact]
    public async Task The_tree_syncs_one_save_starts_run_a_bounded_number_at_once()
    {
        await using var host = new ShopTreeHost(onDisk: true);
        await host.SeedAsync();
        await using (var db = host.Bare())
        {
            db.Shops.AddRange(Enumerable.Range(0, 97).Select(i => new Shop { Name = "Shop " + i, TownID = 1, OwnerID = 1 }));
            await db.SaveChangesAsync(Ct);
            await db.Shops.ExecuteUpdateAsync(s => s.SetProperty(x => x.LastReplicationDate, x => (DateTimeOffset?)x.LastSaveDate), Ct);
        }

        var running = 0;
        var most = 0;
        host.Cosmos.Refuse = write =>
        {
            var now = Interlocked.Increment(ref running);
            for (var seen = Volatile.Read(ref most); now > seen && Interlocked.CompareExchange(ref most, now, seen) != seen; seen = Volatile.Read(ref most)) { }
            Thread.Sleep(15);
            Interlocked.Decrement(ref running);
            return false;
        };

        await host.SaveAsync(async db => (await db.Owners.SingleAsync(x => x.ID == 1, Ct)).Name = "Ana");

        Assert.Equal(98, host.Log.Started.Count);
        Assert.Empty(host.Log.Failed);
        Assert.Equal(98, host.Shops.Count);
        Assert.InRange(most, 2, CosmosDbReplicationSaveObserver.MaxConcurrentTreeSyncs);
        Assert.Equal("[]", await host.MarkedAsync());
    }
}
