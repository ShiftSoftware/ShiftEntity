using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Sqlite;

/// <summary>
/// <c>UseSqlite(..., sqlite => sqlite.UseCaseInsensitiveText())</c>: SQLite's own comparison (with case) is kept unless
/// the consumer turns this on; turned on, every text column without a collation of its own compares and is unique
/// without case, and a column with its own collation or a value converter keeps it.
/// </summary>
public sealed class SqliteCaseInsensitiveTextTests : IAsyncLifetime
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await connection.OpenAsync(Ct);

    public ValueTask DisposeAsync() => connection.DisposeAsync();

    private async Task<Store> CreatedAsync(bool caseInsensitive)
    {
        var options = new DbContextOptionsBuilder<Store>().UseSqlite(connection, sqlite =>
        {
            if (caseInsensitive)
                sqlite.UseCaseInsensitiveText();
        }).Options;
        var db = new Store(options);
        await db.Database.EnsureCreatedAsync(Ct);
        return db;
    }

    [Fact]
    public async Task Without_it_SQLite_compares_text_with_case()
    {
        await using var db = await CreatedAsync(caseInsensitive: false);
        db.Add(new Account { Username = "Dave" });
        await db.SaveChangesAsync(Ct);

        Assert.False(await db.Accounts.AnyAsync(x => x.Username == "dave", Ct));
        Assert.Null(db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(Account))!.FindProperty(nameof(Account.Username))!.GetCollation());
    }

    [Fact]
    public async Task With_it_text_compares_and_is_unique_without_case()
    {
        await using var db = await CreatedAsync(caseInsensitive: true);
        db.Add(new Account { Username = "Dave", Code = "Abc" });
        await db.SaveChangesAsync(Ct);
        db.ChangeTracker.Clear();

        Assert.True(await db.Accounts.AnyAsync(x => x.Username == "DAVE", Ct));
        Assert.Equal(["Dave"], await db.Accounts.Where(x => x.Username.StartsWith("dav")).Select(x => x.Username).ToListAsync(Ct));

        db.Add(new Account { Username = "dave" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task A_column_with_a_collation_of_its_own_or_a_value_converter_keeps_its_comparison()
    {
        await using var db = await CreatedAsync(caseInsensitive: true);
        var account = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(Account))!;

        Assert.Equal("NOCASE", account.FindProperty(nameof(Account.Username))!.GetCollation());
        Assert.Equal("BINARY", account.FindProperty(nameof(Account.Code))!.GetCollation());
        Assert.Null(account.FindProperty(nameof(Account.Upper))!.GetCollation());

        db.Add(new Account { Username = "Erin", Code = "Abc" });
        await db.SaveChangesAsync(Ct);
        Assert.False(await db.Accounts.AnyAsync(x => x.Code == "abc", Ct));
    }

    private sealed class Account
    {
        public long ID { get; set; }
        public string Username { get; set; } = "";
        public string? Code { get; set; }
        public string? Upper { get; set; }
    }

    private sealed class Store(DbContextOptions<Store> options) : DbContext(options)
    {
        public DbSet<Account> Accounts => Set<Account>();

        protected override void OnModelCreating(ModelBuilder builder)
        {
            builder.Entity<Account>(e =>
            {
                e.HasIndex(x => x.Username).IsUnique();
                e.Property(x => x.Code).UseCollation("BINARY");
                e.Property(x => x.Upper).HasConversion(v => v == null ? null : v.ToUpperInvariant(), v => v);
            });
        }
    }
}
