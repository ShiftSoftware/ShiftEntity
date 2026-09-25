using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.CosmosDbReplication;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.Model.Replication;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Replication;

/// <summary>
/// Pins the after-save trigger's bookkeeping write. The trigger records a successful sync from a background task, in
/// a context of its own, while the entity it holds is still the caller's instance: tracked by the caller's context and
/// free to gain related rows before the task gets there (a unit of work with several saves, such as a seeder). The
/// write must change the two replication columns of that one row and nothing else, whatever the caller has linked to
/// it since. Attaching the instance to the task's context walked that graph and inserted the caller's new rows a
/// second time. Runs on SQLite because the write is an ExecuteUpdate, which the in-memory provider cannot run.
/// </summary>
public class ReplicationBookkeepingTests : IDisposable
{
    public sealed class BookkeepingBranch : ShiftEntity<BookkeepingBranch>, IShiftEntityReplication
    {
        public string Name { get; set; } = "";
        public List<BookkeepingLink> Links { get; set; } = new();
        public DateTimeOffset? LastReplicationDate { get; set; }
        public string? LastReplicationStamp { get; set; }
    }

    public sealed class BookkeepingLink : ShiftEntity<BookkeepingLink>
    {
        public string Name { get; set; } = "";
        public long BookkeepingBranchID { get; set; }
    }

    public sealed class BookkeepingDbContext(DbContextOptions options) : ShiftDbContext(options)
    {
        public DbSet<BookkeepingBranch> Branches => Set<BookkeepingBranch>();
        public DbSet<BookkeepingLink> Links => Set<BookkeepingLink>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            // Hosts commonly hide soft-deleted rows; a soft delete still replicates and must still be recorded.
            modelBuilder.Entity<BookkeepingBranch>().HasQueryFilter(x => !x.IsDeleted);
        }
    }

    private readonly SqliteConnection connection = new("DataSource=:memory:");

    public ReplicationBookkeepingTests()
    {
        connection.Open();
        using var db = NewContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => connection.Dispose();

    private BookkeepingDbContext NewContext() =>
        new(new DbContextOptionsBuilder<BookkeepingDbContext>().UseSqlite(connection).Options);

    [Fact]
    public async Task A_row_the_caller_linked_after_the_save_is_inserted_once_by_the_caller()
    {
        var ct = TestContext.Current.CancellationToken;
        using var caller = NewContext();
        var branch = new BookkeepingBranch { Name = "Branch" };
        caller.Branches.Add(branch);
        await caller.SaveChangesAsync(ct);                 // the save whose trigger starts the sync

        branch.Links.Add(new BookkeepingLink { Name = "Link" }); // the caller's next step, before the sync records

        using (var sync = NewContext())
        {
            branch.MarkReplicated("""{"ID":"1"}""");
            Assert.Equal(1, await sync.SaveReplicationBookkeepingAsync(branch, ct));
            Assert.Empty(sync.ChangeTracker.Entries());
        }

        using (var check = NewContext())
        {
            Assert.Equal(0, await check.Links.CountAsync(ct));
            var stored = await check.Branches.AsNoTracking().SingleAsync(ct);
            Assert.Equal(branch.LastSaveDate, stored.LastReplicationDate);
            Assert.Equal("""{"ID":"1"}""", stored.LastReplicationStamp);
        }

        await caller.SaveChangesAsync(ct);                 // the caller's own save of the link

        using (var check = NewContext())
            Assert.Equal("Link", (await check.Links.SingleAsync(ct)).Name);
    }

    [Fact]
    public async Task Only_the_two_replication_columns_of_that_row_change_even_when_it_is_soft_deleted()
    {
        var ct = TestContext.Current.CancellationToken;
        BookkeepingBranch target, other;
        using (var setup = NewContext())
        {
            target = new BookkeepingBranch { Name = "Target", IsDeleted = true };
            other = new BookkeepingBranch { Name = "Other" };
            setup.Branches.AddRange(target, other);
            await setup.SaveChangesAsync(ct);
        }

        target.Name = "Unsaved caller edit";
        target.MarkReplicated("""{"ID":"2"}""");
        using (var sync = NewContext())
            Assert.Equal(1, await sync.SaveReplicationBookkeepingAsync(target, ct));

        using var check = NewContext();
        var rows = await check.Branches.IgnoreQueryFilters().AsNoTracking().ToDictionaryAsync(x => x.ID, ct);
        var stored = rows[target.ID];
        var untouched = rows[other.ID];
        Assert.Equal(("Target", (DateTimeOffset?)target.LastSaveDate, (string?)"""{"ID":"2"}"""),
            (stored.Name, stored.LastReplicationDate, stored.LastReplicationStamp));
        Assert.Equal(("Other", (DateTimeOffset?)null, (string?)null),
            (untouched.Name, untouched.LastReplicationDate, untouched.LastReplicationStamp));
    }
}
