using System.Transactions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.DataLevelAccess;
using ShiftSoftware.ShiftEntity.CosmosDbReplication;
using ShiftSoftware.ShiftEntity.CosmosDbReplication.Services;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.EFCore.Replication;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.ShiftEntity.Model.HashIds;
using ShiftSoftware.ShiftEntity.Model.Replication;
using ShiftSoftware.ShiftEntity.Tests.DataLevelAccess.Scenario;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Replication;

/// <summary>
/// A replication tree on SQL Server with system-versioned tables, as the identity hosts have them: a root and the city
/// it belongs to, both replicated.
/// </summary>
public static class TemporalTreeModel
{
    [TemporalShiftEntity]
    public sealed class TCity : ShiftEntity<TCity>, IShiftEntityReplication
    {
        public string Name { get; set; } = "";
        public DateTimeOffset? LastReplicationDate { get; set; }
        public string? LastReplicationStamp { get; set; }
    }

    [TemporalShiftEntity]
    public sealed class TRoot : ShiftEntity<TRoot>, IShiftEntityReplication
    {
        public string Name { get; set; } = "";
        public long? CityID { get; set; }
        public TCity? City { get; set; }
        public DateTimeOffset? LastReplicationDate { get; set; }
        public string? LastReplicationStamp { get; set; }
    }

    // Not replicated.
    [TemporalShiftEntity]
    public sealed class TNote : ShiftEntity<TNote>
    {
        public string Text { get; set; } = "";
    }

    public sealed class TDb(DbContextOptions<TDb> options) : ShiftDbContext(options)
    {
        public DbSet<TCity> Cities => Set<TCity>();
        public DbSet<TRoot> Roots => Set<TRoot>();
        public DbSet<TNote> Notes => Set<TNote>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<TRoot>().HasReplicationTree(q => q.Include(x => x.City));
        }
    }

    public sealed class TRootDocument
    {
        public string id { get; set; } = "";
        public string Name { get; set; } = "";
        public string? City { get; set; }
    }

    public sealed class TCityDocument
    {
        public string id { get; set; } = "";
        public string Name { get; set; } = "";
    }

    public sealed class TRootDto : ShiftEntityDTOBase
    {
        public override string? ID { get; set; }
    }
}

/// <summary>
/// Pins what replication trees and their bookkeeping do on SQL Server: the revisions list of a system-versioned root,
/// the versions the marks add, the set-based bookkeeping statement, and tree saves inside an ambient or enlisted
/// transaction. Skipped without a local SQL Server (see <see cref="OwnedSqlCatalog"/>).
/// </summary>
public class ReplicationSqlServerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Host : IAsyncDisposable
    {
        private readonly OwnedSqlCatalog catalog;

        public OwnedSqlCatalog Catalog => catalog;
        public ReplicationSyncLog Log { get; } = new();
        public SqlLog Sql { get; } = new();
        public RecordingCosmos Cosmos { get; } = new RecordingCosmos(ReplicationPinHost.Database).Container("roots", "/id").Container("cities", "/id");
        public ServiceProvider Provider { get; }

        private Host(OwnedSqlCatalog catalog)
        {
            this.catalog = catalog;

            var services = new ServiceCollection();
            services.AddLogging(x => x.AddProvider(Log));
            services.AddDbContext<TemporalTreeModel.TDb>(o => o.UseSqlServer(catalog.ConnectionString).UseTemporal(true).AddInterceptors(Sql));
            services.AddShiftEntityCosmosDbReplicationTrigger<TemporalTreeModel.TDb>(o =>
                o.SetUpReplication<TemporalTreeModel.TDb, TemporalTreeModel.TRoot>(Cosmos.Client, ReplicationPinHost.Database)
                    .ReplicateTree<TemporalTreeModel.TRootDocument>("roots", x => x.id, x => new TemporalTreeModel.TRootDocument
                    {
                        id = x.Entity.ID.ToString(),
                        Name = x.Entity.Name,
                        City = x.Entity.City?.Name,
                    }));
            services.AddShiftEntityCosmosDbReplication<TemporalTreeModel.TDb>();

            // What a repository needs, for the revisions list.
            services.AddScoped<ICurrentUserProvider>(_ => FakeCurrentUserProvider.Anonymous());
            services.AddScoped<IdentityClaimProvider>();
            services.AddSingleton<IHashIdService>(new RecordingHashIdService());
            services.AddSingleton<IDefaultDataLevelAccess>(new RecordingDefaultDataLevelAccess());

            Provider = services.BuildServiceProvider(validateScopes: true);
        }

        /// <param name="set">Options of the catalog to set before its tables are created, for example
        /// <c>COMPATIBILITY_LEVEL = 120</c>.</param>
        public static async Task<Host> CreateAsync(string name, string? set = null)
        {
            var catalog = await OwnedSqlCatalog.CreateAsync(name, Ct);
            if (set is not null)
                await catalog.SetAsync(set, Ct);

            var host = new Host(catalog);
            await using var db = host.Bare();
            await db.GetService<IRelationalDatabaseCreator>().CreateTablesAsync(Ct);
            return host;
        }

        public TemporalTreeModel.TDb Bare() =>
            new((DbContextOptions<TemporalTreeModel.TDb>)new DbContextOptionsBuilder<TemporalTreeModel.TDb>()
                .UseSqlServer(catalog.ConnectionString).UseTemporal(true).Options);

        public async Task SaveAsync(Func<TemporalTreeModel.TDb, Task> change)
        {
            await using (var scope = Provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TemporalTreeModel.TDb>();
                await change(db);
                await db.SaveChangesAsync(Ct);
            }
            await Log.AllFinishedAsync();
        }

        // City 1 with roots 1 and 2, city 2 with root 3, every root recorded as replicated.
        public async Task SeedAsync()
        {
            await using var db = Bare();
            var erbil = new TemporalTreeModel.TCity { Name = "Erbil" };
            var basra = new TemporalTreeModel.TCity { Name = "Basra" };
            db.Roots.AddRange(
                new TemporalTreeModel.TRoot { Name = "R1", City = erbil },
                new TemporalTreeModel.TRoot { Name = "R2", City = erbil },
                new TemporalTreeModel.TRoot { Name = "R3", City = basra });
            await db.SaveChangesAsync(Ct);
            await db.Roots.ExecuteUpdateAsync(s => s.SetProperty(x => x.LastReplicationDate, x => (DateTimeOffset?)x.LastSaveDate), Ct);
        }

        public Task RenameCityAsync(long id, string name) =>
            SaveAsync(async db => (await db.Cities.SingleAsync(x => x.ID == id, Ct)).Name = name);

        public Task<List<RevisionDTO>> RevisionsAsync(long id) => RevisionsAsync<TemporalTreeModel.TRoot>(id);

        public async Task<List<RevisionDTO>> RevisionsAsync<T>(long id) where T : ShiftEntity<T>, new()
        {
            await using var scope = Provider.CreateAsyncScope();
            var repository = new ShiftRepository<TemporalTreeModel.TDb, T, TemporalTreeModel.TRootDto, TemporalTreeModel.TRootDto>(
                scope.ServiceProvider.GetRequiredService<TemporalTreeModel.TDb>());
            return (await repository.GetRevisionsAsync(id).ToListAsync(Ct)).OrderBy(x => x.ValidFrom).ToList();
        }

        public Task<int> VersionsAsync(long id) => VersionsAsync<TemporalTreeModel.TRoot>(id);

        public async Task<int> VersionsAsync<T>(long id) where T : ShiftEntity<T>
        {
            await using var db = Bare();
            return await db.Set<T>().TemporalAll().CountAsync(x => x.ID == id, Ct);
        }

        /// <summary>A catch-up of the cities, each as a document of its own, in a scope of the host.</summary>
        public async Task<(CosmosDbReplicationResult Result, IReadOnlyList<string> Sent)> CatchUpCitiesAsync(Func<TemporalTreeModel.TCity, string>? id = null)
        {
            await using var scope = Provider.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<TemporalTreeModel.TDb>();
            Sql.Clear();
            var result = await scope.ServiceProvider.GetRequiredService<CosmosDBReplication>()
                .SetUp<TemporalTreeModel.TDb, TemporalTreeModel.TCity>(Cosmos.Client, ReplicationPinHost.Database)
                .Replicate<TemporalTreeModel.TCityDocument>("cities", x => new TemporalTreeModel.TCityDocument { id = id?.Invoke(x) ?? x.ID.ToString(), Name = x.Name })
                .RunAndReportAsync();
            return (result, Sql.SentBy(context).SkipWhile(x => x.StartsWith("SELECT")).ToList());
        }

        public async Task<string> MarkedAsync()
        {
            await using var db = Bare();
            var ids = await db.Roots.AsNoTracking().Where(x => x.LastReplicationDate == null).Select(x => x.ID).OrderBy(x => x).ToListAsync(Ct);
            return "[" + string.Join(",", ids) + "]";
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Log.AllFinishedAsync();
            }
            finally
            {
                await Provider.DisposeAsync();
                Cosmos.Dispose();
                await catalog.DisposeAsync();
            }
        }
    }

    private static bool IsCurrent(RevisionDTO revision) => revision.ValidTo?.UtcDateTime.Year == DateTime.MaxValue.Year;

    // RV-04. A sync records its watermark on the row, and a part's save marks the root, and on a system-versioned table
    // each write is a version. The revisions list used to show every version as a revision by the row's last editor.
    [Fact]
    public async Task The_revisions_list_shows_the_saves_and_not_the_versions_that_record_replication()
    {
        await using var host = await Host.CreateAsync("Revisions");

        await host.SaveAsync(db =>
        {
            db.Roots.Add(new TemporalTreeModel.TRoot { Name = "R1", City = new TemporalTreeModel.TCity { Name = "Erbil" } });
            return Task.CompletedTask;
        });

        Assert.Equal(2, await host.VersionsAsync(1));       // the insert, and its sync's watermark
        var first = Assert.Single(await host.RevisionsAsync(1));
        Assert.True(IsCurrent(first));

        await host.RenameCityAsync(1, "Erbil 2");
        Assert.Equal(4, await host.VersionsAsync(1));       // the mark, and the tree sync's watermark
        var afterPart = Assert.Single(await host.RevisionsAsync(1));
        Assert.Equal(first.ValidFrom, afterPart.ValidFrom);
        Assert.True(IsCurrent(afterPart));

        // The root's next save, as a repository makes it, is a revision of its own: the first ends where it starts.
        await host.SaveAsync(async db =>
        {
            var root = await db.Roots.SingleAsync(Ct);
            root.Name = "R1 b";
            root.LastSaveDate = DateTimeOffset.UtcNow;
        });
        var revisions = await host.RevisionsAsync(1);
        Assert.Equal(2, revisions.Count);
        Assert.Equal(first.ValidFrom, revisions[0].ValidFrom);
        Assert.Equal(revisions[1].ValidFrom, revisions[0].ValidTo);
        Assert.False(IsCurrent(revisions[0]));
        Assert.True(IsCurrent(revisions[1]));

        // The list is queried the way the revisions endpoint queries it: sorted, paged and counted in SQL.
        await using var scope = host.Provider.CreateAsyncScope();
        var repository = new ShiftRepository<TemporalTreeModel.TDb, TemporalTreeModel.TRoot, TemporalTreeModel.TRootDto, TemporalTreeModel.TRootDto>(
            scope.ServiceProvider.GetRequiredService<TemporalTreeModel.TDb>());
        Assert.Equal(2, await repository.GetRevisionsAsync(1).CountAsync(Ct));
        var latest = await repository.GetRevisionsAsync(1).OrderByDescending(x => x.ValidFrom).Take(1).SingleAsync(Ct);
        Assert.Equal(revisions[1].ValidFrom, latest.ValidFrom);
    }

    // RV-04. Each mark is an UPDATE of a system-versioned root, and so a version. A root that is marked already is not
    // marked again, so a burst of part changes before its sync adds one version, not one for each change.
    [Fact]
    public async Task A_part_change_marks_only_the_roots_not_marked_already()
    {
        await using var host = await Host.CreateAsync("Marks");
        await host.SeedAsync();
        host.Cosmos.Refuse = _ => true;     // the syncs record nothing, so the marks stay
        var before = await host.VersionsAsync(1);

        await host.RenameCityAsync(1, "Erbil 2");
        Assert.Equal("[1,2]", await host.MarkedAsync());
        Assert.Equal(before + 1, await host.VersionsAsync(1));

        await host.RenameCityAsync(1, "Erbil 3");
        Assert.Equal(before + 1, await host.VersionsAsync(1));
    }

    // RV-07, on SQL Server: one UPDATE joined to the values of up to 1,000 rows, passed as one JSON parameter, all in one
    // transaction. The base engine's batched save sent 121 commands for 5,000 rows; this sends 5.
    [Fact]
    public async Task The_catch_up_writes_its_bookkeeping_set_based_on_SQL_Server()
    {
        await using var host = await Host.CreateAsync("Bookkeeping");
        await host.SeedAsync();
        await using (var db = host.Bare())
        {
            db.Cities.AddRange(Enumerable.Range(0, 1_198).Select(i => new TemporalTreeModel.TCity { Name = "City " + i }));
            await db.SaveChangesAsync(Ct);
        }
        host.Cosmos.Refuse = write => write.Id == "7";

        await using var scope = host.Provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TemporalTreeModel.TDb>();
        host.Sql.Clear();
        var result = await scope.ServiceProvider.GetRequiredService<CosmosDBReplication>()
            .SetUp<TemporalTreeModel.TDb, TemporalTreeModel.TCity>(host.Cosmos.Client, ReplicationPinHost.Database)
            .Replicate<TemporalTreeModel.TCityDocument>("cities", x => new TemporalTreeModel.TCityDocument { id = x.ID.ToString(), Name = x.Name })
            .RunAndReportAsync();

        Assert.Equal((1_200, 1_199, 1), (result.Selected, result.Replicated, result.Failed));
        var sent = host.Sql.SentBy(context).SkipWhile(x => x.StartsWith("SELECT")).ToList();
        Assert.Equal(["BEGIN", "UPDATE", "UPDATE", "COMMIT"], sent.Select(x => x.Split(' ')[0]));
        Assert.All(sent.Where(x => x.StartsWith("UPDATE")), x =>
        {
            Assert.StartsWith("UPDATE [t] SET [t].[LastReplicationDate] = [v].[Date]", x);
            Assert.Contains("OPENJSON(@rows)", x);
        });

        await using (var db = host.Bare())
        {
            var cities = await db.Cities.AsNoTracking().ToDictionaryAsync(x => x.ID, Ct);
            Assert.Null(cities[7].LastReplicationDate);
            Assert.All(cities.Values.Where(x => x.ID != 7), x => Assert.Equal((DateTimeOffset?)x.LastSaveDate, x.LastReplicationDate));
            Assert.Equal("100", LastReplicationStamp.Deserialize(cities[100].LastReplicationStamp)?.Id);
        }

        Assert.Equal("[]", await host.MarkedAsync());
    }

    // RV-10. Under an ambient transaction EF refuses a transaction of the context's own, and a tree save used to throw.
    // The ambient transaction holds the save and its mark together, and the sync starts once it commits.
    [Fact]
    public async Task A_tree_save_inside_a_TransactionScope_marks_in_it_and_replicates_after_it_commits()
    {
        await using var host = await Host.CreateAsync("Scope");
        await host.SeedAsync();

        async Task RenameInScopeAsync(string name, bool complete)
        {
            using var ambient = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);
            await using (var scope = host.Provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TemporalTreeModel.TDb>();
                (await db.Cities.SingleAsync(x => x.ID == 2, Ct)).Name = name;
                await db.SaveChangesAsync(Ct);
                Assert.Empty(host.Log.Started);
            }
            if (complete)
                ambient.Complete();
        }

        await RenameInScopeAsync("Basra 2", complete: false);
        await host.Log.AllFinishedAsync();
        Assert.Empty(host.Log.Started);
        Assert.Equal("[]", await host.MarkedAsync());
        await using (var db = host.Bare())
            Assert.Equal("Basra", (await db.Cities.SingleAsync(x => x.ID == 2, Ct)).Name);

        await RenameInScopeAsync("Basra 3", complete: true);
        await host.Log.AllFinishedAsync();
        Assert.Equal(["TRoot 3"], host.Log.Started);
        Assert.Empty(host.Log.Failed);
        Assert.Equal(["""{"id":"3","Name":"R3","City":"Basra 3"}"""], host.Cosmos.StoredLines("roots"));
        Assert.Equal("[]", await host.MarkedAsync());
    }

    // RV-10. The same for a connection enlisted in a transaction by hand.
    [Fact]
    public async Task A_tree_save_on_a_connection_enlisted_in_a_transaction_replicates_after_it_commits()
    {
        await using var host = await Host.CreateAsync("Enlisted");
        await host.SeedAsync();

        using (var transaction = new CommittableTransaction())
        {
            await using (var scope = host.Provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TemporalTreeModel.TDb>();
                await db.Database.OpenConnectionAsync(Ct);
                db.Database.EnlistTransaction(transaction);
                (await db.Cities.SingleAsync(x => x.ID == 2, Ct)).Name = "Basra 2";
                await db.SaveChangesAsync(Ct);
                Assert.Empty(host.Log.Started);
            }
            transaction.Commit();
        }
        await host.Log.AllFinishedAsync();

        Assert.Equal(["TRoot 3"], host.Log.Started);
        Assert.Empty(host.Log.Failed);
        Assert.Equal(["""{"id":"3","Name":"R3","City":"Basra 2"}"""], host.Cosmos.StoredLines("roots"));
        Assert.Equal("[]", await host.MarkedAsync());
    }

    // RV-04. The revisions list used to leave out every version whose LastSaveDate did not move, and a write outside a
    // repository, such as a job's rename through a bare context, leaves LastSaveDate alone. Only the versions in which
    // replication records itself are left out, and only for a replicated type.
    [Fact]
    public async Task A_write_outside_a_repository_is_a_revision_and_only_the_versions_replication_writes_are_left_out()
    {
        await using var host = await Host.CreateAsync("BareWrites");
        long city, note;
        await using (var db = host.Bare())
        {
            var duhok = new TemporalTreeModel.TCity { Name = "Duhok" };
            var memo = new TemporalTreeModel.TNote { Text = "memo" };
            db.AddRange(duhok, memo);
            await db.SaveChangesAsync(Ct);
            (city, note) = (duhok.ID, memo.ID);
        }
        await using (var db = host.Bare())
        {
            (await db.Cities.SingleAsync(x => x.ID == city, Ct)).Name = "Duhok (renamed by a nightly job)";
            (await db.Notes.SingleAsync(x => x.ID == note, Ct)).Text = "memo (edited by a nightly job)";
            await db.SaveChangesAsync(Ct);      // a bare save stamps no LastSaveDate on an update
        }
        await using (var db = host.Bare())
        {
            var duhok = await db.Cities.SingleAsync(x => x.ID == city, Ct);
            duhok.Name = "Duhok 3";
            duhok.LastSaveDate = DateTimeOffset.UtcNow;     // as a repository save stamps it
            await db.SaveChangesAsync(Ct);
        }

        Assert.Equal(3, await host.VersionsAsync<TemporalTreeModel.TCity>(city));
        var revisions = await host.RevisionsAsync<TemporalTreeModel.TCity>(city);
        Assert.Equal(3, revisions.Count);
        Assert.Equal(revisions[1].ValidFrom, revisions[0].ValidTo);
        Assert.Equal(revisions[2].ValidFrom, revisions[1].ValidTo);
        Assert.True(IsCurrent(revisions[2]));

        // A sync's watermark is replication's own write: it belongs to the revision before it.
        await using (var db = host.Bare())
            await db.Cities.Where(x => x.ID == city)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastReplicationDate, x => (DateTimeOffset?)x.LastSaveDate), Ct);
        Assert.Equal(4, await host.VersionsAsync<TemporalTreeModel.TCity>(city));
        var afterSync = await host.RevisionsAsync<TemporalTreeModel.TCity>(city);
        Assert.Equal(revisions.Select(x => x.ValidFrom), afterSync.Select(x => x.ValidFrom));
        Assert.True(IsCurrent(afterSync[2]));

        // A type that is not replicated keeps every version as a revision.
        Assert.Equal(2, (await host.RevisionsAsync<TemporalTreeModel.TNote>(note)).Count);
    }

    // The revisions list as LINQ states it, with correlated subqueries: the definition the repository's one-pass query
    // must give, row for row.
    private static IQueryable<RevisionDTO> Defined<T>(TemporalTreeModel.TDb db, long id) where T : ShiftEntity<T>
    {
        var versions = db.Set<T>().TemporalAll().AsNoTracking().Where(x => x.ID == id);
        var revisions = versions.Where(x => !versions.Any(previous =>
            EF.Property<DateTime>(previous, "PeriodEnd") == EF.Property<DateTime>(x, "PeriodStart") &&
            previous.LastSaveDate == x.LastSaveDate &&
            (EF.Property<DateTimeOffset?>(previous, "LastReplicationDate") != EF.Property<DateTimeOffset?>(x, "LastReplicationDate") ||
                EF.Property<string?>(previous, "LastReplicationStamp") != EF.Property<string?>(x, "LastReplicationStamp"))));

        return revisions.Select(x => new RevisionDTO
        {
            ID = x.ID.ToString(),
            ValidFrom = EF.Property<DateTime>(x, "PeriodStart"),
            ValidTo = revisions
                .Where(next => EF.Property<DateTime>(next, "PeriodStart") > EF.Property<DateTime>(x, "PeriodStart"))
                .Min(next => (DateTime?)EF.Property<DateTime>(next, "PeriodStart"))
                ?? versions.Max(last => EF.Property<DateTime>(last, "PeriodEnd")),
            SavedByUserID = x.LastSavedByUserID == null ? null : x.LastSavedByUserID.ToString(),
        });
    }

    private static async Task<List<(string? ID, DateTimeOffset? From, DateTimeOffset? To, string? By)>> ListAsync(IQueryable<RevisionDTO> revisions) =>
        (await revisions.OrderBy(x => x.ValidFrom).ToListAsync(Ct)).Select(x => (x.ID, x.ValidFrom, x.ValidTo, x.SavedByUserID)).ToList();

    // One write of its own, in a transaction of its own, after the clock has moved on: SQL Server shows no version that
    // starts and ends at the same instant.
    private static async Task WriteAsync(Host host, Func<TemporalTreeModel.TDb, Task> write)
    {
        await Task.Delay(20, Ct);
        await using var db = host.Bare();
        await write(db);
        await db.SaveChangesAsync(Ct);
    }

    // RV-04. The repository reads the row's versions once, with window functions. Each kind of version a row can have is
    // here: replication's own writes (a watermark, a mark, a new stamp alone), a write outside a repository, a stamp that
    // changes only in letter case (equal under the column's collation), saves, and a delete followed by an insert of the
    // same key, whose first version does not start where the last one ended. A deleted row's last revision ends at its
    // delete.
    [Fact]
    public async Task The_revisions_list_gives_its_definition_for_every_kind_of_version()
    {
        await using var host = await Host.CreateAsync("Definition");
        await host.SeedAsync();
        DateTimeOffset save1;
        await using (var db = host.Bare())
            save1 = (await db.Roots.AsNoTracking().SingleAsync(x => x.ID == 1, Ct)).LastSaveDate;

        Task Stamp(string? stamp, bool watermark) => WriteAsync(host, async db =>
        {
            var root = await db.Roots.SingleAsync(x => x.ID == 1, Ct);
            root.LastReplicationStamp = stamp;
            if (watermark)
                root.LastReplicationDate = root.LastSaveDate;
        });

        await Stamp("A", watermark: false);                                                                     // hidden: a new stamp alone
        await WriteAsync(host, async db => (await db.Cities.SingleAsync(x => x.ID == 1, Ct)).Name = "Erbil 2");    // hidden: the mark
        await WriteAsync(host, async db => (await db.Roots.SingleAsync(x => x.ID == 1, Ct)).Name = "R1 by a job"); // a revision
        await Stamp("a", watermark: true);                                                                      // hidden: a watermark
        await Stamp("A", watermark: false);                                                                     // a revision: the same stamp under the collation
        var save2 = DateTimeOffset.UtcNow;
        await WriteAsync(host, async db =>
        {
            var root = await db.Roots.SingleAsync(x => x.ID == 1, Ct);
            root.Name = "R1 saved";
            root.LastSaveDate = save2;                                                                          // a revision: a save
        });
        await Stamp("B", watermark: true);                                                                      // hidden: its watermark
        await WriteAsync(host, async db => db.Roots.Remove(await db.Roots.SingleAsync(x => x.ID == 1, Ct)));
        await Task.Delay(20, Ct);
        await using (var db = host.Bare())
        {
            // The same key again, with the deleted row's save date: its first version follows no version that ends where
            // it starts, so it is a revision although the replication date changed.
            await db.Database.OpenConnectionAsync(Ct);
            var identityInsert = "SET IDENTITY_INSERT " +
                db.GetService<ISqlGenerationHelper>().DelimitIdentifier(db.Model.FindEntityType(typeof(TemporalTreeModel.TRoot))!.GetTableName()!) + " ON";
            await db.Database.ExecuteSqlRawAsync(identityInsert, Ct);
            db.Roots.Add(new TemporalTreeModel.TRoot { ID = 1, Name = "R1 again", CityID = 1, LastSaveDate = save2, LastReplicationDate = save1 });
            await db.SaveChangesAsync(Ct);
        }
        await Stamp("C", watermark: true);                                                                      // hidden: its watermark

        long city;
        await using (var db = host.Bare())
        {
            var kirkuk = new TemporalTreeModel.TCity { Name = "Kirkuk" };
            db.Cities.Add(kirkuk);
            await db.SaveChangesAsync(Ct);
            city = kirkuk.ID;
        }
        await WriteAsync(host, async db => (await db.Cities.SingleAsync(x => x.ID == city, Ct)).LastReplicationDate = DateTimeOffset.UtcNow);
        await WriteAsync(host, async db => db.Cities.Remove(await db.Cities.SingleAsync(x => x.ID == city, Ct)));

        await using var scope = host.Provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TemporalTreeModel.TDb>();
        var roots = new ShiftRepository<TemporalTreeModel.TDb, TemporalTreeModel.TRoot, TemporalTreeModel.TRootDto, TemporalTreeModel.TRootDto>(context);
        var cities = new ShiftRepository<TemporalTreeModel.TDb, TemporalTreeModel.TCity, TemporalTreeModel.TRootDto, TemporalTreeModel.TRootDto>(context);

        Assert.Equal(11, await host.VersionsAsync(1));
        var revisions = await ListAsync(roots.GetRevisionsAsync(1));
        Assert.Equal(await ListAsync(Defined<TemporalTreeModel.TRoot>(context, 1)), revisions);
        Assert.Equal(5, revisions.Count);       // the seed's insert, the job, the stamp in another case, the save, the insert again
        Assert.All(revisions.SkipLast(1).Zip(revisions.Skip(1)), x => Assert.Equal(x.Second.From, x.First.To));
        Assert.Equal(DateTime.MaxValue.Year, revisions[^1].To?.UtcDateTime.Year);

        var deleted = await ListAsync(cities.GetRevisionsAsync(city));
        Assert.Equal(await ListAsync(Defined<TemporalTreeModel.TCity>(context, city)), deleted);
        var only = Assert.Single(deleted);
        Assert.NotEqual(DateTime.MaxValue.Year, only.To?.UtcDateTime.Year);
        await using (var db = host.Bare())
            Assert.Equal(only.To, await db.Cities.TemporalAll().Where(x => x.ID == city).MaxAsync(x => (DateTimeOffset?)EF.Property<DateTime>(x, "PeriodEnd"), Ct));
    }

    // RV-04. The list is an IQueryable, as the revisions endpoint pages and counts it in SQL.
    [Fact]
    public async Task The_revisions_list_is_filtered_sorted_paged_and_counted_in_SQL()
    {
        await using var host = await Host.CreateAsync("Paging");
        await host.SeedAsync();
        for (var i = 0; i < 6; i++)
        {
            await WriteAsync(host, async db =>
            {
                var root = await db.Roots.SingleAsync(x => x.ID == 1, Ct);
                root.Name = "R1 " + i;
                root.LastSaveDate = DateTimeOffset.UtcNow;
            });
            await WriteAsync(host, async db =>
            {
                var root = await db.Roots.SingleAsync(x => x.ID == 1, Ct);
                root.LastReplicationDate = root.LastSaveDate;
            });
        }

        await using var scope = host.Provider.CreateAsyncScope();
        var repository = new ShiftRepository<TemporalTreeModel.TDb, TemporalTreeModel.TRoot, TemporalTreeModel.TRootDto, TemporalTreeModel.TRootDto>(
            scope.ServiceProvider.GetRequiredService<TemporalTreeModel.TDb>());
        var all = (await repository.GetRevisionsAsync(1).ToListAsync(Ct)).OrderBy(x => x.ValidFrom).ToList();

        Assert.Equal(7, all.Count);
        Assert.Equal(7, await repository.GetRevisionsAsync(1).CountAsync(Ct));
        Assert.Equal([all[4].ValidFrom, all[3].ValidFrom],
            await repository.GetRevisionsAsync(1).OrderByDescending(x => x.ValidFrom).Skip(2).Take(2).Select(x => x.ValidFrom).ToListAsync(Ct));
        Assert.Equal(3, await repository.GetRevisionsAsync(1).Where(x => x.ValidFrom > all[3].ValidFrom).CountAsync(Ct));
        Assert.Equal(all[^1].ValidFrom, (await repository.GetRevisionsAsync(1).OrderByDescending(x => x.ValidTo).FirstAsync(Ct)).ValidFrom);
        Assert.False(await repository.GetRevisionsAsync(1).AnyAsync(x => x.ValidTo == null, Ct));
    }

    // RV-04. SQL Server shows only the last version that one transaction writes of a row, so the writes of one
    // transaction are one version: a row added and changed in one is one revision, and a save that a mark of the same
    // root follows in its transaction is the save's revision. A bare save of a root marks the root in the same
    // transaction, so its one version looks like replication's own write and is left out, as the remarks of
    // GetRevisionsAsync say.
    [Fact]
    public async Task The_writes_of_one_transaction_are_one_version_and_a_save_marked_in_it_is_a_revision()
    {
        await using var host = await Host.CreateAsync("OneTransaction");
        await host.SeedAsync();

        long id;
        await using (var db = host.Bare())
        {
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            var city = new TemporalTreeModel.TCity { Name = "Mosul" };
            db.Cities.Add(city);
            await db.SaveChangesAsync(Ct);
            city.Name = "Mosul (checked)";
            await db.SaveChangesAsync(Ct);
            await transaction.CommitAsync(Ct);
            id = city.ID;
        }
        Assert.Equal(1, await host.VersionsAsync<TemporalTreeModel.TCity>(id));
        Assert.True(IsCurrent(Assert.Single(await host.RevisionsAsync<TemporalTreeModel.TCity>(id))));

        // A save of root 1, as a repository makes it, then a change of its city that marks it.
        await using (var db = host.Bare())
        {
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            var root = await db.Roots.SingleAsync(x => x.ID == 1, Ct);
            root.Name = "R1 renamed";
            root.LastSaveDate = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(Ct);
            (await db.Cities.SingleAsync(x => x.ID == 1, Ct)).Name = "Erbil 2";
            await db.SaveChangesAsync(Ct);
            await transaction.CommitAsync(Ct);
        }
        Assert.Equal("[1,2]", await host.MarkedAsync());
        var revisions = await host.RevisionsAsync(1);
        Assert.Equal(2, revisions.Count);       // the seed's insert and the save; the mark is in the save's version
        Assert.Equal(revisions[1].ValidFrom, revisions[0].ValidTo);
        Assert.True(IsCurrent(revisions[1]));

        await using (var db = host.Bare())
        {
            (await db.Roots.SingleAsync(x => x.ID == 3, Ct)).Name = "R3 renamed by a job";
            await db.SaveChangesAsync(Ct);
        }
        Assert.Equal("[1,2,3]", await host.MarkedAsync());
        Assert.True(IsCurrent(Assert.Single(await host.RevisionsAsync(3))));
    }

    // RV-07. OPENJSON needs compatibility level 130, and a database upgraded from an older version keeps its older
    // level. There the bookkeeping used to fail after the Cosmos writes, so every run threw and its rows stayed dirty.
    // Below 130 the rows go as a list of values, a hundred a statement.
    [Fact]
    public async Task Below_compatibility_level_130_the_catch_up_writes_its_bookkeeping_as_a_list_of_values()
    {
        await using var host = await Host.CreateAsync("Level120", "COMPATIBILITY_LEVEL = 120");
        await host.SeedAsync();
        await using (var db = host.Bare())
        {
            db.Cities.AddRange(Enumerable.Range(0, 248).Select(i => new TemporalTreeModel.TCity { Name = "City " + i }));
            await db.SaveChangesAsync(Ct);
        }
        host.Cosmos.Refuse = write => write.Id == "7";

        var (result, sent) = await host.CatchUpCitiesAsync();

        Assert.Equal((250, 249, 1), (result.Selected, result.Replicated, result.Failed));
        Assert.Equal(["BEGIN", "UPDATE", "UPDATE", "UPDATE", "COMMIT"], sent.Select(x => x.Split(' ')[0]));
        Assert.All(sent.Where(x => x.StartsWith("UPDATE")), x =>
        {
            Assert.Contains("INNER JOIN (VALUES (@p0, @p1, @p2), ", x);
            Assert.DoesNotContain("OPENJSON", x);
        });
        await using (var db = host.Bare())
        {
            var cities = await db.Cities.AsNoTracking().ToListAsync(Ct);
            Assert.All(cities.Where(x => x.ID != 7), x => Assert.Equal((DateTimeOffset?)x.LastSaveDate, x.LastReplicationDate));
            Assert.Null(cities.Single(x => x.ID == 7).LastReplicationDate);
        }

        // A part's save marks its roots at this level too, and their syncs record their watermarks.
        await host.RenameCityAsync(1, "Erbil 2");
        Assert.Equal(["TRoot 1", "TRoot 2"], host.Log.Started.Order());
        Assert.Empty(host.Log.Failed);
        Assert.Equal("[]", await host.MarkedAsync());
    }

    // RV-07. The level is read once per database. A database lowered below 130 afterwards answers the JSON statement
    // with an error, and the write then goes as a list of values once, in the same transaction, and from then on.
    [Fact]
    public async Task A_database_lowered_below_level_130_after_its_level_was_read_gets_a_list_of_values()
    {
        await using var host = await Host.CreateAsync("Lowered");
        await host.SeedAsync();

        var (first, sent) = await host.CatchUpCitiesAsync();
        Assert.Equal((2, 2, 0), (first.Selected, first.Replicated, first.Failed));
        Assert.Contains("OPENJSON(@rows)", sent.Single(x => x.StartsWith("UPDATE")));

        await host.Catalog.SetAsync("COMPATIBILITY_LEVEL = 120", Ct);
        async Task DirtyAsync()
        {
            await using var db = host.Bare();
            await db.Cities.ExecuteUpdateAsync(s => s.SetProperty(x => x.LastReplicationDate, (DateTimeOffset?)null), Ct);
        }

        await DirtyAsync();
        var (second, fallback) = await host.CatchUpCitiesAsync();
        Assert.Equal((2, 2, 0), (second.Selected, second.Replicated, second.Failed));
        Assert.Equal(["BEGIN", "UPDATE", "UPDATE", "COMMIT"], fallback.Select(x => x.Split(' ')[0]));
        Assert.Contains("OPENJSON", fallback[1]);
        Assert.Contains("(VALUES", fallback[2]);

        await DirtyAsync();
        var (third, values) = await host.CatchUpCitiesAsync();
        Assert.Equal((2, 2, 0), (third.Selected, third.Replicated, third.Failed));
        Assert.Equal(["BEGIN", "UPDATE", "COMMIT"], values.Select(x => x.Split(' ')[0]));
        Assert.Contains("(VALUES", values[1]);

        await using (var db = host.Bare())
            Assert.All(await db.Cities.AsNoTracking().ToListAsync(Ct), x => Assert.Equal((DateTimeOffset?)x.LastSaveDate, x.LastReplicationDate));
    }

    private static readonly string Odd = "-\"" + (char)0xFC + "<>&'\\/" + (char)0x2028 + char.ConvertFromUtf32(0x1F4A1) + new string('x', 3000);

    // RV-07. Both statements write each row's watermark and stamp exactly: a save date with an offset and seven digits,
    // and a stamp with quotes, markup, a line separator, a character outside the basic plane and 3,000 more characters.
    [Theory]
    [InlineData(null)]
    [InlineData("COMPATIBILITY_LEVEL = 120")]
    public async Task The_bookkeeping_writes_each_watermark_and_stamp_exactly(string? set)
    {
        await using var host = await Host.CreateAsync("RoundTrip", set);
        await using (var db = host.Bare())
        {
            db.Cities.AddRange(Enumerable.Range(0, 1001).Select(i => new TemporalTreeModel.TCity { Name = "C" + i }));
            await db.SaveChangesAsync(Ct);
            var offsetDates = "UPDATE [" + db.Model.FindEntityType(typeof(TemporalTreeModel.TCity))!.GetTableName() +
                "] SET [LastSaveDate] = TODATETIMEOFFSET(DATEADD(NANOSECOND, 1234500, SYSUTCDATETIME()), '+03:00')";
            await db.Database.ExecuteSqlRawAsync(offsetDates, Ct);
        }

        var (result, sent) = await host.CatchUpCitiesAsync(x => x.ID + Odd);
        Assert.Equal((1001, 1001, 0), (result.Selected, result.Replicated, result.Failed));
        Assert.Equal(set is null ? 4 : 13, sent.Count);

        await using (var db = host.Bare())
        {
            var cities = await db.Cities.AsNoTracking().ToListAsync(Ct);
            Assert.All(cities, x =>
            {
                Assert.Equal((DateTimeOffset?)x.LastSaveDate, x.LastReplicationDate);
                Assert.Equal(TimeSpan.FromHours(3), x.LastReplicationDate!.Value.Offset);
                Assert.Equal(x.ID + Odd, LastReplicationStamp.Deserialize(x.LastReplicationStamp)?.Id);
            });
        }

        var (again, _) = await host.CatchUpCitiesAsync(x => x.ID + Odd);
        Assert.Equal(0, again.Selected);
    }

    // RV-10. A context that joined another's transaction with UseTransaction is not told about its commit, so its syncs
    // start at once. On a database with snapshot reads, a tree sync then read the tree as committed before the change,
    // wrote that document, and its watermark cleared the root's mark: the root looked replicated with the old tree.
    [Fact]
    public async Task A_tree_save_in_a_joined_transaction_leaves_its_roots_to_the_catch_up_on_a_snapshot_database()
    {
        await using var host = await Host.CreateAsync("Joined", "READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE");
        await host.SeedAsync();

        await using var shared = new SqlConnection(host.Catalog.ConnectionString);
        await shared.OpenAsync(Ct);
        await using var ownerScope = host.Provider.CreateAsyncScope();
        await using var joinScope = host.Provider.CreateAsyncScope();
        TemporalTreeModel.TDb Context(IServiceProvider services) => new((DbContextOptions<TemporalTreeModel.TDb>)
            new DbContextOptionsBuilder<TemporalTreeModel.TDb>().UseSqlServer(shared).UseTemporal(true)
                .UseShiftEntityAfterCommit().UseApplicationServiceProvider(services).Options);
        await using var owner = Context(ownerScope.ServiceProvider);
        await using var joining = Context(joinScope.ServiceProvider);

        await using (var transaction = await owner.Database.BeginTransactionAsync(Ct))
        {
            await joining.Database.UseTransactionAsync(transaction.GetDbTransaction(), Ct);
            (await joining.Cities.SingleAsync(x => x.ID == 2, Ct)).Name = "Basra 2";
            await joining.SaveChangesAsync(Ct);
            Assert.Empty(host.Log.Started);
            await transaction.CommitAsync(Ct);
        }
        await joining.Database.UseTransactionAsync(null, Ct);
        await host.Log.AllFinishedAsync();

        Assert.Empty(host.Cosmos.Writes);
        Assert.Equal("[3]", await host.MarkedAsync());

        await using (var scope = host.Provider.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<CosmosDBReplication>()
                .SetUp<TemporalTreeModel.TDb, TemporalTreeModel.TRoot>(host.Cosmos.Client, ReplicationPinHost.Database)
                .ReplicateTree<TemporalTreeModel.TRootDocument>("roots", x => new TemporalTreeModel.TRootDocument
                {
                    id = x.ID.ToString(),
                    Name = x.Name,
                    City = x.City?.Name,
                })
                .RunAndReportAsync();
            Assert.Equal((1, 1, 0), (result.Selected, result.Replicated, result.Failed));
        }
        Assert.Equal(["""{"id":"3","Name":"R3","City":"Basra 2"}"""], host.Cosmos.StoredLines("roots"));
        Assert.Equal("[]", await host.MarkedAsync());
    }
}
