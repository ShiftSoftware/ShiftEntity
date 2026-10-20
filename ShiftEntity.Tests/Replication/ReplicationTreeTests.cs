using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.CosmosDbReplication;
using ShiftSoftware.ShiftEntity.CosmosDbReplication.Exceptions;
using ShiftSoftware.ShiftEntity.CosmosDbReplication.Services;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.EFCore.Replication;
using ShiftSoftware.ShiftEntity.Model.Replication;
using Xunit;
using static ShiftSoftware.ShiftEntity.Tests.Replication.TreeModel;

namespace ShiftSoftware.ShiftEntity.Tests.Replication;

/// <summary>
/// A shop whose document is made of its town, district and land, its owner, its products through link rows, and its
/// opening hours in order: a replication tree (<c>HasReplicationTree</c>) with each kind of step.
/// </summary>
public static class TreeModel
{
    public sealed class Land : ShiftEntity<Land>
    {
        public string Name { get; set; } = "";
    }

    public sealed class District : ShiftEntity<District>
    {
        public string Name { get; set; } = "";
        public long LandID { get; set; }
        public Land? Land { get; set; }
    }

    public sealed class Town : ShiftEntity<Town>
    {
        public string Name { get; set; } = "";
        public long DistrictID { get; set; }
        public District? District { get; set; }
    }

    // Replicated as a document of its own too, as a city or a service is: its own syncs record their two replication
    // columns, and that must not mark the shops it belongs to.
    public sealed class Owner : ShiftEntity<Owner>, IShiftEntityReplication
    {
        public string Name { get; set; } = "";
        public DateTimeOffset? LastReplicationDate { get; set; }
        public string? LastReplicationStamp { get; set; }
    }

    public sealed class Product : ShiftEntity<Product>
    {
        public string Name { get; set; } = "";
    }

    public sealed class Shop : ShiftEntity<Shop>, IShiftEntityReplication
    {
        public string Name { get; set; } = "";
        public long? TownID { get; set; }
        public Town? Town { get; set; }
        public long? OwnerID { get; set; }
        public Owner? Owner { get; set; }
        public List<ShopProduct> Products { get; set; } = new();
        public List<Opening> Openings { get; set; } = new();
        public DateTimeOffset? LastReplicationDate { get; set; }
        public string? LastReplicationStamp { get; set; }
    }

    public sealed class ShopProduct : ShiftEntity<ShopProduct>
    {
        public long ShopID { get; set; }
        public Shop? Shop { get; set; }
        public long ProductID { get; set; }
        public Product? Product { get; set; }
    }

    public sealed class Opening : ShiftEntity<Opening>
    {
        public long ShopID { get; set; }
        public int Day { get; set; }
    }

    // In no tree.
    public sealed class Unrelated : ShiftEntity<Unrelated>
    {
        public string Text { get; set; } = "";
    }

    public sealed class Db(DbContextOptions<Db> options) : ShiftDbContext(options)
    {
        public DbSet<Land> Lands => Set<Land>();
        public DbSet<District> Districts => Set<District>();
        public DbSet<Town> Towns => Set<Town>();
        public DbSet<Owner> Owners => Set<Owner>();
        public DbSet<Product> Products => Set<Product>();
        public DbSet<Shop> Shops => Set<Shop>();
        public DbSet<ShopProduct> ShopProducts => Set<ShopProduct>();
        public DbSet<Opening> Openings => Set<Opening>();
        public DbSet<Unrelated> Unrelated => Set<Unrelated>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<Shop>().HasReplicationTree(q => q
                .Include(x => x.Town).ThenInclude(x => x!.District).ThenInclude(x => x!.Land)
                .Include(x => x.Owner)
                .Include(x => x.Products).ThenInclude(x => x.Product)
                .Include(x => x.Openings.OrderBy(o => o.Day)));
        }
    }

    public sealed class ShopDocument
    {
        public string id { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Town { get; set; }
        public string? District { get; set; }
        public string? Land { get; set; }
        public string? Owner { get; set; }
        public List<string> Products { get; set; } = new();
        public List<int> Days { get; set; } = new();
    }

    // The same tree on a context no other test uses, with a set EF fills in without building the model.
    public sealed class FirstUseDb(DbContextOptions<FirstUseDb> options) : ShiftDbContext(options)
    {
        public DbSet<Shop> Shops { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<Shop>().HasReplicationTree(q => q.Include(x => x.Openings.OrderBy(o => o.Day)));
        }
    }

    // The document as the shop's own row would give it, without its tree.
    public sealed class FlatShopDocument
    {
        public string id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Kind { get; set; } = "Shop";
    }

    public sealed class OwnerDocument
    {
        public string id { get; set; } = "";
        public string Name { get; set; } = "";
    }

    public static OwnerDocument Document(Owner x) => new() { id = x.ID.ToString(), Name = x.Name };

    public static ShopDocument Document(Shop x) => new()
    {
        id = x.ID.ToString(),
        Name = x.Name,
        Town = x.Town?.Name,
        District = x.Town?.District?.Name,
        Land = x.Town?.District?.Land?.Name,
        Owner = x.Owner?.Name,
        Products = x.Products.Select(p => p.Product?.Name ?? "?").ToList(),
        Days = x.Openings.Select(o => o.Day).ToList(),
    };

    public static FlatShopDocument Flat(Shop x) => new() { id = x.ID.ToString(), Name = x.Name };
}

/// <summary>
/// Pins replication from a tree (S3 of the replication aggregates plan): the declaration, the walk up from a changed
/// row to its roots along the model's foreign keys, the set-based mark inside the save's transaction, and the sync
/// that loads each root by ID with its tree after the commit. The catch-up builds the same document.
/// </summary>
public class ReplicationTreeTests : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Records the text of every command each context instance sends.
    private sealed class Commands : DbCommandInterceptor
    {
        private readonly ConcurrentDictionary<DbContext, ConcurrentQueue<string>> sent = new();

        public IReadOnlyList<string> SentBy(DbContext context) =>
            sent.TryGetValue(context, out var texts) ? texts.ToList() : [];

        public void Clear() => sent.Clear();

        private void Record(CommandEventData data) =>
            sent.GetOrAdd(data.Context!, _ => new()).Enqueue(data.Command.CommandText);

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Record(eventData);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Record(eventData);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            Record(eventData);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Record(eventData);
            return ValueTask.FromResult(result);
        }
    }

    private readonly SqliteConnection connection = new("DataSource=:memory:");
    private readonly ReplicationSyncLog log = new();
    private readonly RecordingCosmos cosmos = new RecordingCosmos(ReplicationPinHost.Database)
        .Container("shops", "/id")
        .Container("flat", "/Kind");
    private readonly Commands commands = new();
    private ServiceProvider? provider;

    private ServiceProvider Host()
    {
        connection.Open();

        var services = new ServiceCollection();
        services.AddLogging(x => x.AddProvider(log));
        services.AddDbContext<Db>(o => o.UseSqlite(connection).AddInterceptors(commands));
        services.AddShiftEntityCosmosDbReplicationTrigger<Db>(o =>
            o.SetUpReplication<Db, Shop>(cosmos.Client, ReplicationPinHost.Database)
                .ReplicateTree<ShopDocument>("shops", x => x.id, x => Document(x.Entity)));
        services.AddShiftEntityCosmosDbReplication<Db>();

        provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<Db>().Database.EnsureCreated();
        return provider;
    }

    public async ValueTask DisposeAsync()
    {
        await log.AllFinishedAsync();
        if (provider is not null)
            await provider.DisposeAsync();
        cosmos.Dispose();
        await connection.DisposeAsync();
    }

    private Db Bare() => new(new DbContextOptionsBuilder<Db>().UseSqlite(connection).Options);

    private async Task<Shop> StoredAsync(long id)
    {
        await using var db = Bare();
        return await db.Shops.AsNoTracking().SingleAsync(x => x.ID == id, Ct);
    }

    // Two lands, districts and towns, two owners and three products; shop 1 in town 1 of owner 1, shop 2 in town 2 of
    // owner 2, shop 3 in town 1 of owner 2. Written without replication, then each shop recorded as replicated.
    private async Task SeedAsync()
    {
        await using var db = Bare();
        var lands = new[] { new Land { Name = "North" }, new Land { Name = "South" } };
        var districts = new[] { new District { Name = "Hills", Land = lands[0] }, new District { Name = "Coast", Land = lands[1] } };
        var towns = new[] { new Town { Name = "Oak", District = districts[0] }, new Town { Name = "Pier", District = districts[1] } };
        var owners = new[] { new Owner { Name = "Ada" }, new Owner { Name = "Bo" } };
        var products = new[] { new Product { Name = "Bread" }, new Product { Name = "Milk" }, new Product { Name = "Tea" } };
        db.AddRange(lands);
        db.AddRange(districts);
        db.AddRange(towns);
        db.AddRange(owners);
        db.AddRange(products);
        db.Shops.AddRange(
            new Shop { Name = "Corner", Town = towns[0], Owner = owners[0] },
            new Shop { Name = "Dock", Town = towns[1], Owner = owners[1] },
            new Shop { Name = "Mill", Town = towns[0], Owner = owners[1] });
        await db.SaveChangesAsync(Ct);
        await db.Shops.ExecuteUpdateAsync(s => s.SetProperty(x => x.LastReplicationDate, x => (DateTimeOffset?)x.LastSaveDate), Ct);
    }

    private async Task SaveAsync(Func<Db, Task> change)
    {
        await using var scope = provider!.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Db>();
        await change(db);
        await db.SaveChangesAsync(Ct);
        await log.AllFinishedAsync();
    }

    private IReadOnlyList<string> Shops => cosmos.StoredLines("shops");

    [Fact]
    public async Task A_link_row_names_its_root_by_its_own_key_and_the_root_is_rebuilt_from_its_tree()
    {
        Host();
        await SeedAsync();

        Db? saving = null;
        await SaveAsync(db =>
        {
            saving = db;
            db.ShopProducts.Add(new ShopProduct { ShopID = 1, ProductID = 2 });
            return Task.CompletedTask;
        });

        Assert.Equal(["Shop 1"], log.Started);
        Assert.Equal(["""{"id":"1","Name":"Corner","Town":"Oak","District":"Hills","Land":"North","Owner":"Ada","Products":["Milk"],"Days":[]}"""], Shops);

        // The save found the root without a query: its own INSERT, then the mark of the root, and nothing else.
        var sent = commands.SentBy(saving!);
        Assert.Equal(2, sent.Count);
        Assert.StartsWith("INSERT INTO \"ShopProducts\"", sent[0]);
        Assert.StartsWith("UPDATE \"Shops\"", sent[1]);

        var stored = await StoredAsync(1);
        Assert.Equal((DateTimeOffset?)stored.LastSaveDate, stored.LastReplicationDate);
        Assert.Equal("1", LastReplicationStamp.Deserialize(stored.LastReplicationStamp)?.Id);
    }

    [Fact]
    public async Task A_part_a_root_points_at_rebuilds_the_roots_that_point_at_it_and_no_other()
    {
        Host();
        await SeedAsync();

        await SaveAsync(async db => (await db.Owners.SingleAsync(x => x.ID == 2, Ct)).Name = "Bea");

        Assert.Equal(["Shop 2", "Shop 3"], log.Started.Order());
        Assert.Equal(
        [
            """{"id":"2","Name":"Dock","Town":"Pier","District":"Coast","Land":"South","Owner":"Bea","Products":[],"Days":[]}""",
            """{"id":"3","Name":"Mill","Town":"Oak","District":"Hills","Land":"North","Owner":"Bea","Products":[],"Days":[]}""",
        ], Shops);
    }

    [Fact]
    public async Task A_part_three_steps_away_rebuilds_its_roots()
    {
        Host();
        await SeedAsync();

        await SaveAsync(async db => (await db.Lands.SingleAsync(x => x.ID == 1, Ct)).Name = "Uplands");

        Assert.Equal(["Shop 1", "Shop 3"], log.Started.Order());
        Assert.All(Shops, x => Assert.Contains("\"Land\":\"Uplands\"", x));
    }

    [Fact]
    public async Task An_ordered_collection_keeps_its_order_in_the_document()
    {
        Host();
        await SeedAsync();

        await SaveAsync(db =>
        {
            db.Openings.AddRange(new Opening { ShopID = 1, Day = 5 }, new Opening { ShopID = 1, Day = 1 }, new Opening { ShopID = 1, Day = 3 });
            return Task.CompletedTask;
        });

        Assert.Equal(["""{"id":"1","Name":"Corner","Town":"Oak","District":"Hills","Land":"North","Owner":"Ada","Products":[],"Days":[1,3,5]}"""], Shops);
    }

    [Fact]
    public async Task The_mark_rolls_back_with_the_save_and_nothing_replicates()
    {
        Host();
        await SeedAsync();
        var before = (await StoredAsync(2)).LastReplicationDate;

        await using (var scope = provider!.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Db>();
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            (await db.Owners.SingleAsync(x => x.ID == 2, Ct)).Name = "Bea";
            await db.SaveChangesAsync(Ct);

            Assert.Null((await db.Shops.AsNoTracking().SingleAsync(x => x.ID == 2, Ct)).LastReplicationDate);   // marked
            Assert.Empty(log.Started);

            await transaction.RollbackAsync(Ct);
        }

        await log.AllFinishedAsync();
        Assert.Empty(log.Started);
        Assert.Empty(cosmos.Writes);
        Assert.Equal(before, (await StoredAsync(2)).LastReplicationDate);
    }

    [Fact]
    public async Task Inside_an_explicit_transaction_the_changed_roots_replicate_after_the_commit()
    {
        Host();
        await SeedAsync();

        await using (var scope = provider!.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Db>();
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            (await db.Lands.SingleAsync(x => x.ID == 2, Ct)).Name = "Lowlands";
            await db.SaveChangesAsync(Ct);

            Assert.Empty(log.Started);
            await transaction.CommitAsync(Ct);
            Assert.Equal(["Shop 2"], log.Started);
        }

        await log.AllFinishedAsync();
        Assert.Equal(["""{"id":"2","Name":"Dock","Town":"Pier","District":"Coast","Land":"Lowlands","Owner":"Bo","Products":[],"Days":[]}"""], Shops);
        var stored = await StoredAsync(2);
        Assert.Equal((DateTimeOffset?)stored.LastSaveDate, stored.LastReplicationDate);
    }

    [Fact]
    public async Task A_refused_document_leaves_the_root_marked_for_the_catch_up_and_no_audit_column_changes()
    {
        Host();
        await SeedAsync();
        var before = await StoredAsync(1);
        cosmos.Refuse = _ => true;

        await SaveAsync(db =>
        {
            db.ShopProducts.Add(new ShopProduct { ShopID = 1, ProductID = 1 });
            return Task.CompletedTask;
        });

        var after = await StoredAsync(1);
        Assert.Null(after.LastReplicationDate);
        Assert.Equal((before.CreateDate, before.CreatedByUserID, before.LastSaveDate, before.LastSavedByUserID, before.IsDeleted),
            (after.CreateDate, after.CreatedByUserID, after.LastSaveDate, after.LastSavedByUserID, after.IsDeleted));

        // The catch-up builds the same document from the same tree.
        cosmos.Refuse = _ => false;
        await using var scope = provider!.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<CosmosDBReplication>()
            .SetUp<Db, Shop>(cosmos.Client, ReplicationPinHost.Database)
            .ReplicateTree<ShopDocument>("shops", Document)
            .RunAndReportAsync();

        Assert.Equal((1, 1, 0), (result.Selected, result.Replicated, result.Failed));
        Assert.Equal(["""{"id":"1","Name":"Corner","Town":"Oak","District":"Hills","Land":"North","Owner":"Ada","Products":["Bread"],"Days":[]}"""], Shops);
        var caughtUp = await StoredAsync(1);
        Assert.Equal((DateTimeOffset?)caughtUp.LastSaveDate, caughtUp.LastReplicationDate);
    }

    [Fact]
    public async Task A_save_of_a_type_in_no_tree_sends_only_its_own_command()
    {
        Host();
        await SeedAsync();

        Db? saving = null;
        await SaveAsync(db =>
        {
            saving = db;
            db.Unrelated.Add(new Unrelated { Text = "x" });
            return Task.CompletedTask;
        });

        Assert.Equal(1, commands.SentBy(saving!).Count);
        Assert.Empty(log.Started);
    }

    // A host's own backfill can be the first thing a new context does, before anything has needed its model.
    [Fact]
    public void A_new_contexts_first_query_finds_its_tree()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        using var context = new FirstUseDb(new DbContextOptionsBuilder<FirstUseDb>().UseSqlite(connection).Options);

        var query = context.Shops.IncludeReplicationTree(context);

        Assert.True(context.HasReplicationTree(typeof(Shop)));
        Assert.Contains(nameof(Shop.Openings), query.Expression.ToString());
    }

    [Fact]
    public async Task A_saved_root_is_built_from_its_tree_and_the_callers_instance_is_left_alone()
    {
        Host();
        await SeedAsync();

        var shop = new Shop { Name = "Kiosk", TownID = 2, OwnerID = 1 };
        await SaveAsync(db =>
        {
            db.Shops.Add(shop);
            return Task.CompletedTask;
        });

        Assert.Contains("""{"id":"4","Name":"Kiosk","Town":"Pier","District":"Coast","Land":"South","Owner":"Ada","Products":[],"Days":[]}""", Shops);
        Assert.Null(shop.Town);
        Assert.Null(shop.Owner);
        Assert.Null(shop.LastReplicationDate);

        await SaveAsync(db =>
        {
            db.Shops.Remove(db.Shops.Attach(shop).Entity);
            return Task.CompletedTask;
        });

        Assert.DoesNotContain(Shops, x => x.Contains("\"id\":\"4\""));
    }

    [Fact]
    public async Task A_form_save_of_a_new_root_with_its_links_marks_nothing()
    {
        Host();
        await SeedAsync();

        Db? saving = null;
        await SaveAsync(db =>
        {
            saving = db;
            var shop = new Shop { Name = "Kiosk", TownID = 1, OwnerID = 1 };
            shop.Products.AddRange([new ShopProduct { ProductID = 1 }, new ShopProduct { ProductID = 2 }, new ShopProduct { ProductID = 3 }]);
            db.Shops.Add(shop);
            return Task.CompletedTask;
        });

        Assert.DoesNotContain(commands.SentBy(saving!), x => x.StartsWith("UPDATE") || x.Contains("EXISTS"));
        Assert.Equal(["Shop 4"], log.Started);
        Assert.Equal(1, cosmos.Writes.Count);
        Assert.Contains("\"Products\":[\"Bread\",\"Milk\",\"Tea\"]", Shops.Single());
    }

    [Fact]
    public async Task A_root_edited_without_advancing_its_save_date_is_marked_too()
    {
        Host();
        await SeedAsync();
        cosmos.Refuse = _ => true;     // keeps the mark visible

        await SaveAsync(async db => (await db.Shops.SingleAsync(x => x.ID == 1, Ct)).Name = "Corner Shop");

        Assert.Null((await StoredAsync(1)).LastReplicationDate);
    }

    // A registration replicates one document a row, its own or its tree's: the row's stamp, its watermark and its hard
    // delete each describe that one document. A part's change records the root's watermark after rebuilding the tree
    // document only, so the watermark could not stand for another document, nor for reference updates.
    [Fact]
    public void A_registration_replicates_its_own_document_or_its_tree_never_both()
    {
        var options = new ShiftEntityCosmosDbOptions(new ServiceCollection().BuildServiceProvider());

        var ownFirst = options.SetUpReplication<Db, Shop>(cosmos.Client, ReplicationPinHost.Database);
        ownFirst.Replicate<FlatShopDocument>("flat", x => x.Kind, x => Flat(x.Entity));
        var error = Assert.Throws<ReplicationRegistrationException>(() =>
            ownFirst.ReplicateTree<ShopDocument>("shops", x => x.id, x => Document(x.Entity)));
        Assert.Contains("registers both Replicate and ReplicateTree", error.Message);

        var treeFirst = options.SetUpReplication<Db, Shop>(cosmos.Client, ReplicationPinHost.Database);
        var tree = treeFirst.ReplicateTree<ShopDocument>("shops", x => x.id, x => Document(x.Entity));
        Assert.Throws<ReplicationRegistrationException>(() => treeFirst.Replicate<FlatShopDocument>("flat", x => x.Kind, x => Flat(x.Entity)));
        Assert.Contains("updates no references", Assert.Throws<ReplicationRegistrationException>(() =>
            tree.UpdateReference<FlatShopDocument>("flat", (q, e) => q.Where(x => x.id == e.Entity.ID.ToString()))).Message);
        Assert.Throws<ReplicationRegistrationException>(() =>
            tree.UpdatePropertyReference<FlatShopDocument, FlatShopDocument>("flat", x => x.Name, (q, e) => q.Where(x => x.id == e.Entity.ID.ToString())));

        var referencesFirst = options.SetUpReplication<Db, Shop>(cosmos.Client, ReplicationPinHost.Database);
        referencesFirst.Replicate<FlatShopDocument>("flat", x => x.Kind, x => Flat(x.Entity))
            .UpdateReference<FlatShopDocument>("flat", (q, e) => q.Where(x => x.id == e.Entity.ID.ToString()));
        Assert.Throws<ReplicationRegistrationException>(() =>
            referencesFirst.ReplicateTree<ShopDocument>("shops", x => x.id, x => Document(x.Entity)));
    }

    [Fact]
    public async Task A_root_replicated_from_its_tree_alone_keeps_its_stamp_and_a_hard_delete_removes_its_document()
    {
        Host();
        await SeedAsync();

        await SaveAsync(async db => (await db.Shops.SingleAsync(x => x.ID == 1, Ct)).Name = "Corner Shop");
        Assert.Equal("1", LastReplicationStamp.Deserialize((await StoredAsync(1)).LastReplicationStamp)?.Id);

        await SaveAsync(async db => (await db.Owners.SingleAsync(x => x.ID == 1, Ct)).Name = "Ana");
        Assert.Contains("\"Owner\":\"Ana\"", Shops.Single());

        await SaveAsync(async db => db.Shops.Remove(await db.Shops.SingleAsync(x => x.ID == 1, Ct)));
        Assert.Empty(Shops);
    }
}
