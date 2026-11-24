using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.EFCore;
using Xunit;
using static ShiftSoftware.ShiftEntity.Tests.Replication.PinModel;

namespace ShiftSoftware.ShiftEntity.Tests.Replication;

/// <summary>
/// Pins the seam that the framework's own parts ride on: <see cref="ShiftDbContext"/> reports the rows each save wrote
/// to its <see cref="IShiftDbContextSaveObserver"/>s, and <see cref="ShiftDbContext.AfterCommit(Action)"/> holds work
/// until an explicit transaction commits. The observer is internal: replication and the notification rules implement
/// it, and app code takes part in a save through handlers.
/// </summary>
public class ShiftDbContextSaveObserverTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly SqliteConnection connection = new("DataSource=:memory:");

    public ShiftDbContextSaveObserverTests() => connection.Open();

    public void Dispose() => connection.Dispose();

    // Records each report as one line per row, and queues one after-commit action per report.
    internal sealed class RecordingObserver : IShiftDbContextSaveObserver
    {
        public List<string> Reports { get; } = new();
        public List<string> Committed { get; } = new();

        /// <summary>The entity types the observer asks for. Null for every type.</summary>
        public Type[]? Wanted { get; init; }

        public bool Wants(ShiftDbContext context, Type entityType) => Wanted is null || Wanted.Contains(entityType);

        public ValueTask SavedAsync(ShiftDbContext context, SaveReport saved, CancellationToken cancellationToken)
        {
            var report = string.Join(", ", saved.Rows.Select(x => $"{x.Change} {x.EntityType.Name} {((ShiftEntityBase)x.Entity).ID}"));
            Reports.Add(report);
            context.AfterCommit(() => Committed.Add(report));
            return ValueTask.CompletedTask;
        }
    }

    // Adds a note for every new item before it is saved, as a host's before-save handler might.
    public sealed class AddNoteBeforeSave : IBeforeSaveHandler<Item>
    {
        public ValueTask BeforeSaveAsync(PendingRows<Item> rows, CancellationToken cancellationToken)
        {
            foreach (var row in rows)
                if (row.Change == RowChange.Added)
                    rows.Db.Add(new Note { Text = "added by a before-save handler" });

            return ValueTask.CompletedTask;
        }
    }

    // Saves a log row from its after-save stage, on the same context.
    public sealed class SaveLogAfterSave : IAfterSaveHandler<Item>
    {
        public async ValueTask AfterSaveAsync(SavedRows<Item> rows, CancellationToken cancellationToken)
        {
            if (!rows.Any(row => row.Change == RowChange.Added))
                return;

            rows.Db.Add(new Log { Text = "saved from an after-save handler" });
            await rows.Db.SaveChangesAsync(cancellationToken);
        }
    }

    private (ServiceProvider Provider, RecordingObserver Observer) Host(Action<IServiceCollection>? services = null, Type[]? wanted = null)
    {
        var observer = new RecordingObserver { Wanted = wanted };
        var collection = new ServiceCollection();
        collection.AddDbContext<Db>(o => o.UseSqlite(connection));
        collection.AddSingleton<IShiftDbContextSaveObserver>(observer);
        services?.Invoke(collection);

        var provider = collection.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<Db>().Database.EnsureCreated();
        return (provider, observer);
    }

    [Fact]
    public async Task Each_save_reports_the_rows_it_wrote_with_how_it_changed_them()
    {
        var (provider, observer) = Host();
        await using var _ = provider;
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Db>();

        var item = new Item { Code = "a" };
        db.Items.Add(item);
        await db.SaveChangesAsync(Ct);

        item.Code = "b";
        await db.SaveChangesAsync(Ct);

        db.Items.Remove(item);
        await db.SaveChangesAsync(Ct);

        await db.SaveChangesAsync(Ct);      // a save that writes nothing reports nothing

        Assert.Equal(["Added Item 1", "Modified Item 1", "Deleted Item 1"], observer.Reports);
        Assert.Equal(observer.Reports, observer.Committed);    // no explicit transaction: run at once
    }

    [Fact]
    public async Task A_row_a_before_save_handler_added_is_reported_and_a_save_from_an_after_save_handler_reports_its_own_rows_after()
    {
        var (provider, observer) = Host(services: s => s
            .AddSaveHandler<AddNoteBeforeSave>()
            .AddSaveHandler<SaveLogAfterSave>());
        await using var _ = provider;
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Db>();

        db.Items.Add(new Item { Code = "a" });
        await db.SaveChangesAsync(Ct);

        // The observers of a save hear about it before its after-save handlers run, so the save a handler makes is
        // reported after the save it was made from.
        Assert.Equal(["Added Item 1, Added Note 1", "Added Log 1"], observer.Reports);

        // Both saves ran in the one transaction the context opened, so what they queued ran at its commit, in order.
        Assert.Equal(observer.Reports, observer.Committed);
    }

    [Fact]
    public async Task An_observer_is_reported_only_the_rows_of_the_types_it_asks_for_and_the_context_collects_no_others()
    {
        var (provider, observer) = Host(wanted: [typeof(Item)]);
        await using var _ = provider;
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Db>();

        db.Items.Add(new Item { Code = "a" });
        db.Notes.AddRange(new Note { Text = "one" }, new Note { Text = "two" });
        await db.SaveChangesAsync(Ct);

        Assert.Equal(["Added Item 1"], observer.Reports);
        Assert.Equal(1, db.RowsCollected);

        // A save of rows that nobody asked for is not reported at all.
        db.Notes.Add(new Note { Text = "three" });
        await db.SaveChangesAsync(Ct);

        Assert.Equal(["Added Item 1"], observer.Reports);
        Assert.Equal(1, db.RowsCollected);
    }

    [Fact]
    public async Task Saves_under_SuppressReplication_report_nothing_and_scopes_nest()
    {
        var (provider, observer) = Host();
        await using var _ = provider;
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Db>();

        using (db.SuppressReplication())
        {
            using (db.SuppressReplication())
            {
                db.Items.Add(new Item { Code = "a" });
                await db.SaveChangesAsync(Ct);
            }

            db.Items.Add(new Item { Code = "b" });
            await db.SaveChangesAsync(Ct);
        }

        db.Items.Add(new Item { Code = "c" });
        await db.SaveChangesAsync(Ct);

        Assert.Equal(["Added Item 3"], observer.Reports);
    }

    [Fact]
    public async Task Inside_an_explicit_transaction_work_waits_for_the_commit_and_is_dropped_on_rollback()
    {
        var (provider, observer) = Host();
        await using var _ = provider;
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Db>();

        await using (var transaction = await db.Database.BeginTransactionAsync(Ct))
        {
            db.Items.Add(new Item { Code = "a" });
            await db.SaveChangesAsync(Ct);
            db.Items.Add(new Item { Code = "b" });
            await db.SaveChangesAsync(Ct);

            Assert.Empty(observer.Committed);
            await transaction.RollbackAsync(Ct);
        }

        Assert.Empty(observer.Committed);
        db.ChangeTracker.Clear();       // the rolled-back rows are gone, and their keys are free again

        await using (var transaction = await db.Database.BeginTransactionAsync(Ct))
        {
            db.Items.Add(new Item { Code = "c" });
            await db.SaveChangesAsync(Ct);

            Assert.Empty(observer.Committed);
            await transaction.CommitAsync(Ct);
        }

        // Every save reported its rows when it ran; only the committed one's work ran.
        Assert.Equal(["Added Item 1", "Added Item 2", "Added Item 1"], observer.Reports);
        Assert.Equal(["Added Item 1"], observer.Committed);
    }

    [Fact]
    public async Task A_context_without_observers_reports_nothing()
    {
        await using var bare = new Db(new DbContextOptionsBuilder<Db>().UseSqlite(connection).Options);

        await bare.Database.EnsureCreatedAsync(Ct);
        bare.Items.Add(new Item { Code = "a" });
        await bare.SaveChangesAsync(Ct);
        bare.AfterCommit(() => { });     // no DI, no transaction: runs at once, and nothing breaks

        Assert.Equal(0, bare.RowsCollected);
    }

    [Fact]
    public async Task A_save_that_does_not_accept_its_changes_is_refused_when_an_observer_wants_its_rows()
    {
        var (provider, observer) = Host(wanted: [typeof(Item)]);
        await using var _ = provider;
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Db>();

        // After such a save EF reports no row as written, so the observer could not be told. The save used to go
        // through and the observer heard nothing.
        var item = new Item { Code = "b" };
        db.Items.Add(item);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(acceptAllChangesOnSuccess: false, Ct));
        Assert.Contains("cannot save Item rows with acceptAllChangesOnSuccess: false", refused.Message);
        Assert.Equal(0, await db.Items.AsNoTracking().CountAsync(Ct));

        // Under the observer's switch the save is EF's own. So is a save of rows no observer wants.
        using (db.SuppressReplication())
            await db.SaveChangesAsync(acceptAllChangesOnSuccess: false, Ct);
        db.ChangeTracker.AcceptAllChanges();

        db.Notes.Add(new Note { Text = "nobody asked for notes" });
        await db.SaveChangesAsync(acceptAllChangesOnSuccess: false, Ct);
        db.ChangeTracker.AcceptAllChanges();

        Assert.Empty(observer.Reports);
        Assert.Equal((1, 1), (await db.Items.AsNoTracking().CountAsync(Ct), await db.Notes.AsNoTracking().CountAsync(Ct)));
    }
}
