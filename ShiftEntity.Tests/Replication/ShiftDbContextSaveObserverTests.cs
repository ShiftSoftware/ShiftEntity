using EntityFrameworkCore.Triggered;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.EFCore;
using Xunit;
using static ShiftSoftware.ShiftEntity.Tests.Replication.PinModel;

namespace ShiftSoftware.ShiftEntity.Tests.Replication;

/// <summary>
/// Pins the save pipeline that replication now rides on: <see cref="ShiftDbContext"/> reports the rows each save wrote
/// to its <see cref="IShiftDbContextSaveObserver"/>s, and <see cref="ShiftDbContext.AfterCommit"/> holds work until an
/// explicit transaction commits.
/// </summary>
public class ShiftDbContextSaveObserverTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly SqliteConnection connection = new("DataSource=:memory:");

    public ShiftDbContextSaveObserverTests() => connection.Open();

    public void Dispose() => connection.Dispose();

    // Records each report as one line per row, and queues one after-commit action per report.
    public sealed class RecordingObserver : IShiftDbContextSaveObserver
    {
        public List<string> Reports { get; } = new();
        public List<string> Committed { get; } = new();

        public ValueTask SavedAsync(ShiftDbContext context, SaveReport saved, CancellationToken cancellationToken)
        {
            var report = string.Join(", ", saved.Rows.Select(x => $"{x.Change} {x.EntityType.Name} {((ShiftEntityBase)x.Entity).ID}"));
            Reports.Add(report);
            context.AfterCommit(() => Committed.Add(report));
            return ValueTask.CompletedTask;
        }
    }

    // Adds a note before every item is saved, as a host's before-save step might.
    public sealed class AddNoteBeforeSave(Db db) : IBeforeSaveTrigger<Item>
    {
        public Task BeforeSave(ITriggerContext<Item> context, CancellationToken cancellationToken)
        {
            if (context.ChangeType == ChangeType.Added)
                db.Notes.Add(new Note { Text = "added by a before-save step" });
            return Task.CompletedTask;
        }
    }

    // Saves a log row from its after-save step, on the same context.
    public sealed class SaveLogAfterSave(Db db) : IAfterSaveTrigger<Item>
    {
        public async Task AfterSave(ITriggerContext<Item> context, CancellationToken cancellationToken)
        {
            if (context.ChangeType != ChangeType.Added)
                return;

            db.Logs.Add(new Log { Text = "saved from an after-save step" });
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private (ServiceProvider Provider, RecordingObserver Observer) Host(bool afterCommit = true, Action<IServiceCollection>? services = null)
    {
        var observer = new RecordingObserver();
        var collection = new ServiceCollection();
        collection.AddDbContext<Db>(o =>
        {
            o.UseSqlite(connection).UseTriggers();
            if (afterCommit)
                o.UseShiftEntityAfterCommit();
        });
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
    public async Task A_row_a_before_save_step_added_is_reported_and_a_save_from_an_after_save_step_reports_its_own_rows_first()
    {
        var (provider, observer) = Host(services: s => s
            .AddScoped<IBeforeSaveTrigger<Item>, AddNoteBeforeSave>()
            .AddScoped<IAfterSaveTrigger<Item>, SaveLogAfterSave>());
        await using var _ = provider;
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Db>();

        db.Items.Add(new Item { Code = "a" });
        await db.SaveChangesAsync(Ct);

        Assert.Equal(["Added Log 1", "Added Item 1, Added Note 1"], observer.Reports);
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
    public async Task Without_the_after_commit_interceptor_work_runs_at_once_even_inside_a_transaction()
    {
        var (provider, observer) = Host(afterCommit: false);
        await using var _ = provider;
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Db>();

        await using (var transaction = await db.Database.BeginTransactionAsync(Ct))
        {
            db.Items.Add(new Item { Code = "a" });
            await db.SaveChangesAsync(Ct);

            Assert.Equal(["Added Item 1"], observer.Committed);
            await transaction.RollbackAsync(Ct);
        }
    }

    [Fact]
    public async Task A_context_without_observers_reports_nothing_and_a_save_that_does_not_accept_its_changes_reports_nothing()
    {
        await using (var bare = new Db(new DbContextOptionsBuilder<Db>().UseSqlite(connection).Options))
        {
            await bare.Database.EnsureCreatedAsync(Ct);
            bare.Items.Add(new Item { Code = "a" });
            await bare.SaveChangesAsync(Ct);
            bare.AfterCommit(() => { });     // no DI, no transaction: runs at once, and nothing breaks
        }

        var (provider, observer) = Host();
        await using var _ = provider;
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Db>();
        db.Items.Add(new Item { Code = "b" });
        await db.SaveChangesAsync(acceptAllChangesOnSuccess: false, Ct);

        Assert.Empty(observer.Reports);
    }
}
