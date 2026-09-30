using System.Net;
using Microsoft.Azure.Cosmos;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.CosmosDbReplication;
using ShiftSoftware.ShiftEntity.CosmosDbReplication.Exceptions;
using ShiftSoftware.ShiftEntity.CosmosDbReplication.Services;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.Model.Enums;
using ShiftSoftware.ShiftEntity.Model.Replication;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Replication;

/// <summary>
/// Pins what a catch-up run (the replicate-* functions and the hourly timers) tells its caller. A row that fails does
/// not stop the others and stays dirty for the next run, as before, and the run now says which rows failed, in which
/// container and why: in its report and in a warning. It used to swallow those errors, so a caller reported "Synced".
/// Runs on SQLite, which also pins the dirty-only selection there (SQLite cannot compare DateTimeOffset in SQL, so that
/// selection threw), against a Cosmos DB stand-in that refuses one document.
/// </summary>
public class CatchUpReportTests : IDisposable
{
    public sealed class CatchUpItem : ShiftEntity<CatchUpItem>, IShiftEntityReplication
    {
        public string Name { get; set; } = "";
        public DateTimeOffset? LastReplicationDate { get; set; }
        public string? LastReplicationStamp { get; set; }
    }

    public sealed class CatchUpDbContext(DbContextOptions<CatchUpDbContext> options) : ShiftDbContext(options)
    {
        public DbSet<CatchUpItem> Items => Set<CatchUpItem>();
    }

    public sealed class CatchUpDocument
    {
        public string id { get; set; } = "";
        public string Name { get; set; } = "";
    }

    private sealed class WarningLog(List<string> warnings) : ILoggerProvider, ILogger
    {
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
                lock (warnings)
                    warnings.Add(formatter(state, exception));
        }

        public void Dispose() { }
    }

    private const string Container = "items";
    private readonly SqliteConnection connection = new("DataSource=:memory:");
    private readonly List<string> warnings = new();
    private readonly ServiceProvider provider;

    //The names of the documents Cosmos DB refuses. A test can change it between runs.
    private readonly HashSet<string> refused = ["Refused"];
    private readonly CosmosClient cosmos;

    public CatchUpReportTests()
    {
        cosmos = RefusingCosmos(refused);
        connection.Open();
        var services = new ServiceCollection();
        services.AddLogging(x => x.AddProvider(new WarningLog(warnings)));
        services.AddDbContext<CatchUpDbContext>(o => o.UseSqlite(connection));
        services.AddShiftEntityCosmosDbReplication<CatchUpDbContext>();
        provider = services.BuildServiceProvider(validateScopes: true);
    }

    public void Dispose()
    {
        provider.Dispose();
        connection.Dispose();
    }

    //Accepts every document except the ones named in refused, which it answers with 429.
    private static CosmosClient RefusingCosmos(HashSet<string> refused)
    {
        var properties = Substitute.For<ContainerResponse>();
        properties.Resource.Returns(new ContainerProperties(Container, "/id"));

        var container = Substitute.For<Microsoft.Azure.Cosmos.Container>();
        container.ReadContainerAsync(Arg.Any<ContainerRequestOptions>(), Arg.Any<CancellationToken>()).Returns(properties);
        container.UpsertItemAsync(Arg.Any<CatchUpDocument>(), Arg.Any<PartitionKey?>(), Arg.Any<ItemRequestOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(call => refused.Contains(call.Arg<CatchUpDocument>()?.Name ?? "")
                ? Task.FromException<ItemResponse<CatchUpDocument>>(
                    new CosmosException("Request rate is large.", HttpStatusCode.TooManyRequests, 3200, "activity", 0))
                : Task.FromResult(Substitute.For<ItemResponse<CatchUpDocument>>()));

        var database = Substitute.For<Database>();
        database.GetContainer(Container).Returns(container);

        var client = Substitute.For<CosmosClient>();
        client.ClientOptions.Returns(new CosmosClientOptions());
        client.GetDatabase("catch-up").Returns(database);
        return client;
    }

    private static CatchUpDocument Map(CatchUpItem item) => item.Name == "Unmappable"
        ? throw new InvalidOperationException("No document for this row.")
        : new CatchUpDocument { id = item.ID.ToString(), Name = item.Name };

    //Good, Refused and Unmappable are dirty (never replicated); Clean is recorded as replicated.
    private async Task<Dictionary<string, long>> SeedAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatchUpDbContext>();
        await db.Database.EnsureCreatedAsync(ct);

        var items = new[] { "Good", "Refused", "Unmappable", "Clean" }.Select(x => new CatchUpItem { Name = x }).ToList();
        db.Items.AddRange(items);
        await db.SaveChangesAsync(ct);

        //The stamp a sync of this row records: its id, which is also the partition key.
        var clean = items[3];
        var id = clean.ID.ToString();
        await db.SaveReplicationBookkeepingAsync<CatchUpItem>(clean.ID, clean.LastSaveDate, new LastReplicationStamp
        {
            Id = id,
            Level1 = new PartitionKeyLevelStamp { Value = id, Type = PartitionKeyTypes.String }
        }.Serialize(), ct);

        return items.ToDictionary(x => x.Name, x => x.ID);
    }

    private async Task<Dictionary<string, CatchUpItem>> RowsAsync()
    {
        using var scope = provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CatchUpDbContext>().Items.AsNoTracking()
            .ToDictionaryAsync(x => x.Name, TestContext.Current.CancellationToken);
    }

    private CosmosDbReferenceOperation<CatchUpDbContext, CatchUpItem> Operation(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<CosmosDBReplication>()
            .SetUp<CatchUpDbContext, CatchUpItem>(cosmos, "catch-up")
            .Replicate<CatchUpDocument>(Container, Map);

    [Fact]
    public async Task Failed_rows_are_reported_with_their_reason_and_stay_dirty_while_the_others_replicate()
    {
        var ids = await SeedAsync();

        CosmosDbReplicationResult result;
        using (var scope = provider.CreateScope())
            result = await Operation(scope).RunAndReportAsync();

        //Dirty-only on SQLite: the clean row is not selected.
        Assert.Equal((3, 1, 2, false), (result.Selected, result.Replicated, result.Failed, result.Succeeded));
        Assert.Collection(result.Failures,
            x => Assert.Equal((ids["Refused"], Container,
                    "Cosmos DB answered 429 TooManyRequests (substatus 3200): Request rate is large."),
                (x.EntityId, x.ContainerId, x.Error)),
            x => Assert.Equal((ids["Unmappable"], Container, "InvalidOperationException: No document for this row."),
                (x.EntityId, x.ContainerId, x.Error)));

        var rows = await RowsAsync();
        Assert.Equal(rows["Good"].LastSaveDate, rows["Good"].LastReplicationDate);
        Assert.Null(rows["Refused"].LastReplicationDate);
        Assert.Null(rows["Unmappable"].LastReplicationDate);

        var warning = Assert.Single(warnings);
        Assert.Contains("CatchUpItem: 2 of 3 rows failed and stay dirty for the next run.", warning);
        Assert.Contains($"ID {ids["Refused"]} in '{Container}'", warning);
    }

    [Fact]
    public async Task RunAsync_still_completes_and_warns_and_ThrowIfFailed_fails_the_caller()
    {
        var ids = await SeedAsync();

        using (var scope = provider.CreateScope())
            await Operation(scope).RunAsync();
        Assert.Single(warnings);

        //The next run retries only what is still dirty; a full run selects every row.
        using (var scope = provider.CreateScope())
            Assert.Equal(2, (await Operation(scope).RunAndReportAsync()).Selected);

        CosmosDbReplicationResult full;
        using (var scope = provider.CreateScope())
            full = await Operation(scope).RunAndReportAsync(updateAll: true);
        Assert.Equal((4, 2), (full.Selected, full.Replicated));

        var exception = Assert.Throws<CosmosDbReplicationException>(full.ThrowIfFailed);
        Assert.Same(full, exception.Result);
        Assert.StartsWith("CatchUpItem: 2 of 4 rows failed", exception.Message);
        Assert.DoesNotContain(full.Failures, x => x.EntityId == ids["Good"] || x.EntityId == ids["Clean"]);
    }

    [Fact]
    public async Task A_replicated_row_that_fails_in_a_forced_run_is_retried_by_the_next_plain_run()
    {
        var ids = await SeedAsync();
        var stamp = (await RowsAsync())["Clean"].LastReplicationStamp;

        //Cosmos DB now refuses the clean row too, as a busy container does when the SDK has used up its retries. A
        //forced run selects the row with all the others.
        refused.Add("Clean");

        CosmosDbReplicationResult forced;
        using (var scope = provider.CreateScope())
            forced = await Operation(scope).RunAndReportAsync(updateAll: true);

        Assert.Equal((4, 1, 3), (forced.Selected, forced.Replicated, forced.Failed));
        Assert.Contains(forced.Failures, x => x.EntityId == ids["Clean"]);

        //The row is dirty now. Its stamp stays: it still gives the coordinates of the document of its last successful
        //sync.
        var clean = (await RowsAsync())["Clean"];
        Assert.Null(clean.LastReplicationDate);
        Assert.Equal(stamp, clean.LastReplicationStamp);

        //Cosmos DB accepts the row again. A plain run selects only dirty rows, and it selects and replicates this one.
        refused.Remove("Clean");

        CosmosDbReplicationResult plain;
        using (var scope = provider.CreateScope())
            plain = await Operation(scope).RunAndReportAsync();

        Assert.Equal((3, 1), (plain.Selected, plain.Replicated));
        Assert.DoesNotContain(plain.Failures, x => x.EntityId == ids["Clean"]);

        clean = (await RowsAsync())["Clean"];
        Assert.Equal(clean.LastSaveDate, clean.LastReplicationDate);
    }
}
