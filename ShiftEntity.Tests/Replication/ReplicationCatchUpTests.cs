using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ShiftSoftware.ShiftEntity.CosmosDbReplication;
using ShiftSoftware.ShiftEntity.CosmosDbReplication.Services;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.EFCore.Replication;
using Xunit;
using static ShiftSoftware.ShiftEntity.Tests.Replication.TreeModel;

namespace ShiftSoftware.ShiftEntity.Tests.Replication;

/// <summary>
/// Pins how a catch-up run loads replication tree roots, what it does when a step's container fails, and how it writes
/// its bookkeeping.
/// </summary>
public class ReplicationCatchUpTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task MarkAsync(ShopTreeHost host, params long[] ids)
    {
        await using var db = host.Bare();
        await db.Shops.Where(x => ids.Contains(x.ID)).ExecuteUpdateAsync(s => s.SetProperty(x => x.LastReplicationDate, (DateTimeOffset?)null), Ct);
    }

    // RV-14. The run's context is shared by its steps, and an earlier step may have loaded a root's children. Loaded with
    // tracking, the root's collection started with those, out of the ordered step's order.
    [Fact]
    public async Task The_catch_up_keeps_an_ordered_step_in_order_when_its_context_already_tracks_the_children()
    {
        await using var host = new ShopTreeHost();
        await host.SeedAsync();
        await host.SaveAsync(db =>
        {
            db.Openings.AddRange(new Opening { ShopID = 1, Day = 5 }, new Opening { ShopID = 1, Day = 1 }, new Opening { ShopID = 1, Day = 3 });
            return Task.CompletedTask;
        });
        var saved = host.Shops.Single();
        Assert.Contains("\"Days\":[1,3,5]", saved);

        await MarkAsync(host, 1);
        var result = await host.CatchUpAsync(before: async db => await db.Openings.OrderByDescending(x => x.Day).ToListAsync(Ct));

        Assert.Equal((1, 1, 0), (result.Selected, result.Replicated, result.Failed));
        Assert.Equal(saved, host.Shops.Single());
        Assert.Equal("[]", await host.MarkedAsync());
    }

    public sealed class FilteredDb(DbContextOptions<FilteredDb> options) : ShiftDbContext(options)
    {
        public DbSet<Shop> Shops => Set<Shop>();
        public DbSet<Opening> Openings => Set<Opening>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Opening>().HasQueryFilter(x => !x.IsDeleted);
            modelBuilder.Entity<Shop>().HasReplicationTree(q => q.Include(x => x.Owner).Include(x => x.Openings.OrderBy(o => o.Day)));
        }
    }

    // RV-14. The save side loads a root ignoring query filters; the catch-up used to apply them, and left out the rows
    // they hide.
    [Fact]
    public async Task The_catch_up_builds_the_document_the_save_does_under_a_query_filter()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var log = new ReplicationSyncLog();
        using var cosmos = new RecordingCosmos(ReplicationPinHost.Database).Container("shops", "/id");
        var services = new ServiceCollection();
        services.AddLogging(x => x.AddProvider(log));
        services.AddDbContext<FilteredDb>(o => o.UseSqlite(connection));
        services.AddShiftEntityCosmosDbReplicationTrigger<FilteredDb>(o =>
            o.SetUpReplication<FilteredDb, Shop>(cosmos.Client, ReplicationPinHost.Database)
                .ReplicateTree<ShopDocument>("shops", x => x.id, x => Document(x.Entity)));
        services.AddShiftEntityCosmosDbReplication<FilteredDb>();
        await using var provider = services.BuildServiceProvider(validateScopes: true);
        FilteredDb Bare() => new(new DbContextOptionsBuilder<FilteredDb>().UseSqlite(connection).Options);

        await using (var db = Bare())
        {
            await db.Database.EnsureCreatedAsync(Ct);
            var shop = new Shop { Name = "Corner", Owner = new Owner { Name = "Ada" } };
            shop.Openings.AddRange([new Opening { Day = 1 }, new Opening { Day = 3 }]);
            db.Add(shop);
            await db.SaveChangesAsync(Ct);
            await db.Shops.ExecuteUpdateAsync(s => s.SetProperty(x => x.LastReplicationDate, x => (DateTimeOffset?)x.LastSaveDate), Ct);
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FilteredDb>();
            (await db.Openings.SingleAsync(x => x.Day == 3, Ct)).IsDeleted = true;
            await db.SaveChangesAsync(Ct);
        }
        await log.AllFinishedAsync();
        var saved = cosmos.StoredLines("shops").Single();
        Assert.Contains("\"Days\":[1,3]", saved);

        await using (var db = Bare())
            await db.Shops.ExecuteUpdateAsync(s => s.SetProperty(x => x.LastReplicationDate, (DateTimeOffset?)null), Ct);

        CosmosDbReplicationResult result;
        await using (var scope = provider.CreateAsyncScope())
            result = await scope.ServiceProvider.GetRequiredService<CosmosDBReplication>()
                .SetUp<FilteredDb, Shop>(cosmos.Client, ReplicationPinHost.Database)
                .ReplicateTree<ShopDocument>("shops", Document)
                .RunAndReportAsync();

        Assert.Equal(1, result.Replicated);
        Assert.Equal(saved, cosmos.StoredLines("shops").Single());
    }

    // RV-19. A container that could not be read used to throw out of the run, so a host's catch-up of several steps
    // (branches, then companies, then teams) stopped there.
    [Fact]
    public async Task A_catch_up_step_whose_container_fails_reports_its_rows_and_the_next_step_runs()
    {
        await using var host = new ShopTreeHost();
        await host.SeedAsync();
        await MarkAsync(host, 1, 2, 3);

        await using var scope = host.Provider.CreateAsyncScope();
        var replication = scope.ServiceProvider.GetRequiredService<CosmosDBReplication>();

        var shops = await replication.SetUp<Db, Shop>(host.Cosmos.Client, ReplicationPinHost.Database)
            .ReplicateTree<ShopDocument>("missing", Document)
            .RunAndReportAsync();

        Assert.Equal((3, 0, 3), (shops.Selected, shops.Replicated, shops.Failed));
        Assert.All(shops.Failures, x => Assert.Equal(("missing", "InvalidOperationException: The test declared no container 'missing'."),
            (x.ContainerId, x.Error)));
        Assert.Equal("[1,2,3]", await host.MarkedAsync());

        var owners = await replication.SetUp<Db, Owner>(host.Cosmos.Client, ReplicationPinHost.Database)
            .Replicate<OwnerDocument>("owners", Document)
            .RunAndReportAsync();

        Assert.Equal((2, 2, 0), (owners.Selected, owners.Replicated, owners.Failed));
        Assert.Equal(2, host.Cosmos.StoredLines("owners").Count);
    }

    // RV-07. The bookkeeping used to be one UPDATE per row, each in a transaction of its own: 41 times the statements of
    // the batched save it replaced. On SQLite it is one UPDATE of up to 100 rows at a time now, all in one transaction,
    // and like that save it runs no host triggers, no audit stamping, and marks no root of a tree the rows belong to.
    [Fact]
    public async Task The_catch_up_writes_its_bookkeeping_set_based_in_one_transaction_and_marks_no_root()
    {
        await using var host = new ShopTreeHost();
        await host.SeedAsync();
        await using (var db = host.Bare())
        {
            db.Owners.AddRange(Enumerable.Range(0, 1_198).Select(i => new Owner { Name = "Owner " + i }));
            await db.SaveChangesAsync(Ct);
        }

        await using var scope = host.Provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<Db>();
        host.Sql.Clear();

        var result = await scope.ServiceProvider.GetRequiredService<CosmosDBReplication>()
            .SetUp<Db, Owner>(host.Cosmos.Client, ReplicationPinHost.Database)
            .Replicate<OwnerDocument>("owners", Document)
            .RunAndReportAsync();

        Assert.Equal((1_200, 1_200, 0), (result.Selected, result.Replicated, result.Failed));
        Assert.Equal(["BEGIN", .. Enumerable.Repeat("UPDATE", 12), "COMMIT"],
            host.Sql.SentBy(context).SkipWhile(x => x.StartsWith("SELECT")).Select(x => x.Split(' ')[0]));

        await using (var db = host.Bare())
        {
            var owners = await db.Owners.AsNoTracking().ToListAsync(Ct);
            Assert.All(owners, x => Assert.Equal((DateTimeOffset?)x.LastSaveDate, x.LastReplicationDate));
            Assert.All(owners, x => Assert.Equal(x.ID.ToString(), LastReplicationStamp.Deserialize(x.LastReplicationStamp)?.Id));
        }

        Assert.Equal("[]", await host.MarkedAsync());
        Assert.Empty(context.ChangeTracker.Entries().Where(x => x.State != EntityState.Unchanged));
    }
}
