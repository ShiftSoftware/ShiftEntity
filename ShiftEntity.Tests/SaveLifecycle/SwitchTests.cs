using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.Tests.SaveHandlers;
using Xunit;
using static ShiftSoftware.ShiftEntity.Tests.SaveHandlers.SavePipelineModel;

namespace ShiftSoftware.ShiftEntity.Tests.SaveLifecycle;

/// <summary>
/// The switches of a save. Each says what it switches off: one handler, the handlers of named stages, every handler,
/// or everything that tells other systems. A guard is switched off only by name. A switch applies to the saves that
/// start while it is on, and to their rows through to the commit.
/// </summary>
public class SwitchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Writes a line for each stage, under the name of the class that derives from it.</summary>
    public abstract class Staged<TEntity>(SaveLog log)
        : IBeforeSaveHandler<TEntity>, IAfterSaveHandler<TEntity>, IBeforeCommitHandler<TEntity>, IAfterCommitHandler<TEntity> where TEntity : class
    {
        private ValueTask Write<TRows>(string stage, TRows rows) where TRows : RowSet<TEntity>
        {
            log.Add($"{GetType().Name} {stage}: {string.Join(", ", rows.Select(row => $"{row.Change} '{(row.Entity as INamed)?.Name}'"))}");
            return ValueTask.CompletedTask;
        }

        public ValueTask BeforeSaveAsync(PendingRows<TEntity> rows, CancellationToken cancellationToken) => Write("before save", rows);
        public ValueTask AfterSaveAsync(SavedRows<TEntity> rows, CancellationToken cancellationToken) => Write("after save", rows);
        public ValueTask BeforeCommitAsync(SavedRows<TEntity> rows, CancellationToken cancellationToken) => Write("before commit", rows);
        public ValueTask AfterCommitAsync(SavedRows<TEntity> rows, CancellationToken cancellationToken) => Write("after commit", rows);
    }

    public sealed class Push(SaveLog log) : Staged<Order>(log);
    public sealed class Mail(SaveLog log) : Staged<Order>(log);

    /// <summary>A check that can refuse a save, in the three stages that still can.</summary>
    [Guard]
    public sealed class Lock(SaveLog log) : IBeforeSaveHandler<Order>, IAfterSaveHandler<Order>, IBeforeCommitHandler<Order>
    {
        private ValueTask Check(string stage, IEnumerable<ChangedRow<Order>> rows)
        {
            log.Add($"Lock {stage}");

            if (rows.Any(row => row.Entity.Name == "locked"))
                throw new InvalidOperationException("Orders are locked.");

            return ValueTask.CompletedTask;
        }

        public ValueTask BeforeSaveAsync(PendingRows<Order> rows, CancellationToken cancellationToken) => Check("before save", rows);
        public ValueTask AfterSaveAsync(SavedRows<Order> rows, CancellationToken cancellationToken) => Check("after save", rows);
        public ValueTask BeforeCommitAsync(SavedRows<Order> rows, CancellationToken cancellationToken) => Check("before commit", rows);
    }

    private static string[] Stages(string handler, string rows, params string[] stages) =>
        stages.Select(stage => $"{handler} {stage}: {rows}").ToArray();

    private static readonly string[] Every = ["before save", "after save", "before commit", "after commit"];

    // ── One handler ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task SuppressSaveHandler_switches_one_handler_off_in_every_stage_it_implements_and_nothing_else()
    {
        await using var host = SavePipelineHost.Start(services => services
            .AddSaveHandler<Push>()
            .AddSaveHandler<Mail>()
            .AddScoped<IShiftDbContextSaveObserver, LoggingSaveObserver>());

        await host.InScopeAsync(async db =>
        {
            db.Add(new Order { Name = "a" });

            using (db.SuppressSaveHandler<Push>())
                await db.SaveChangesAsync(Ct);
        });

        // The other handler ran in every stage, and so did the framework's own part.
        Assert.Equal(
        [
            "Mail before save: Added 'a'",
            "observer: Added Order, Added Address",
            "Mail after save: Added 'a'",
            "Mail before commit: Added 'a'",
            "Mail after commit: Added 'a'",
        ], host.Log.Lines);
    }

    [Fact]
    public async Task SuppressSaveHandler_does_nothing_in_a_host_without_the_handler_and_refuses_a_class_that_is_no_handler()
    {
        await using var host = SavePipelineHost.Start(services => services.AddSaveHandler<Mail>());

        await host.InScopeAsync(async db =>
        {
            db.Add(new Order { Name = "a" });

            // A shared service may name a handler that only some of its hosts have.
            using (db.SuppressSaveHandler<Push>())
                await db.SaveChangesAsync(Ct);

            var refused = Assert.Throws<ArgumentException>(() => db.SuppressSaveHandler<SaveLog>());
            Assert.Contains("SaveLog is not a save handler", refused.Message);
        });

        Assert.Equal(Stages("Mail", "Added 'a'", Every), host.Log.Lines);
    }

    // ── Named stages ────────────────────────────────────────────────────────────

    [Fact]
    public async Task SuppressSaveHandlers_switches_the_named_stages_off_and_a_before_save_numbering_handler_keeps_running()
    {
        await using var host = SavePipelineHost.Start(services => services
            .AddSaveHandler<BeforeSaveTests.TicketNumbering>()
            .AddSaveHandler<StageLog<Ticket>>()
            .AddSaveHandler<Push>());

        await host.InScopeAsync(async db =>
        {
            // An import: nothing is told to other systems, and the tickets are still numbered.
            using (db.SuppressSaveHandlers(SaveStages.AfterCommit))
            {
                db.AddRange(new Ticket { Queue = "a", Name = "one" }, new Ticket { Queue = "a", Name = "two" });
                db.Add(new Order { Name = "a" });
                await db.SaveChangesAsync(Ct);
            }
        });

        Assert.Equal(
        [
            "Push before save: Added 'a'",
            "after save of Ticket: Added Ticket 'one', Added Ticket 'two'",
            "Push after save: Added 'a'",
            "before commit of Ticket: Added Ticket 'one', Added Ticket 'two'",
            "Push before commit: Added 'a'",
        ], host.Log.Lines);
        Assert.Equal([1, 2], await host.InScopeAsync(db => db.Tickets.Select(x => x.Number).OrderBy(x => x).ToListAsync(Ct)));
    }

    [Fact]
    public async Task SuppressSaveHandlers_takes_several_stages_and_a_save_with_no_stage_left_that_needs_one_opens_no_transaction()
    {
        await using var host = SavePipelineHost.Start(services => services.AddSaveHandler<Push>());

        await host.InScopeAsync(async db =>
        {
            db.Add(new Order { Name = "a" });

            using (db.SuppressSaveHandlers(SaveStages.AfterSave | SaveStages.BeforeCommit))
                await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(Stages("Push", "Added 'a'", "before save", "after commit"), host.Log.Lines);

        // The stages that make a save need a transaction were switched off for it.
        Assert.Equal(["INSERT Orders"], host.Sql.Lines);
    }

    [Theory]
    [InlineData(SaveStages.None)]
    [InlineData((SaveStages)16)]
    public async Task SuppressSaveHandlers_with_no_stage_throws(SaveStages stages)
    {
        await using var host = SavePipelineHost.Start();

        await host.InScopeAsync(db =>
        {
            // A switch that names nothing is a mistake, and there is no default that could hide a handler added later.
            var refused = Assert.Throws<ArgumentException>(() => db.SuppressSaveHandlers(stages));
            Assert.Contains("SuppressSaveHandlers needs the stages to switch off", refused.Message);
            return Task.CompletedTask;
        });
    }

    // ── Every handler, and the guards ───────────────────────────────────────────

    [Fact]
    public async Task SuppressAllSaveHandlers_switches_every_handler_off_except_the_guards_and_no_part_of_the_framework()
    {
        await using var host = SavePipelineHost.Start(services => services
            .AddSaveHandler<Push>()
            .AddSaveHandler<Mail>()
            .AddSaveHandler<Lock>()
            .AddScoped<IShiftDbContextSaveObserver, LoggingSaveObserver>());

        await host.InScopeAsync(async db =>
        {
            var order = new Order { Name = "a" };
            db.Add(order);

            using (db.SuppressAllSaveHandlers())
                await db.SaveChangesAsync(Ct);

            // Stamped, as by any save.
            Assert.NotEqual(default, order.CreateDate);
        });

        Assert.Equal(["Lock before save", "observer: Added Order, Added Address", "Lock after save", "Lock before commit"], host.Log.Lines);
    }

    [Fact]
    public async Task A_guard_still_refuses_under_the_blanket_switches_and_only_naming_it_switches_it_off()
    {
        await using var host = SavePipelineHost.Start(services => services.AddSaveHandler<Lock>().AddSaveHandler<Push>());

        await host.InScopeAsync(async db =>
        {
            var locked = new Order { Name = "locked" };
            db.Add(locked);

            using (db.SuppressAllSaveHandlers())
                Assert.Equal("Orders are locked.", (await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct))).Message);

            using (db.SuppressSaveHandlers(SaveStages.BeforeSave | SaveStages.AfterSave | SaveStages.BeforeCommit | SaveStages.AfterCommit))
                Assert.Equal("Orders are locked.", (await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct))).Message);

            using (db.SuppressAfterCommit())
                Assert.Equal("Orders are locked.", (await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct))).Message);

            Assert.Equal(0, await db.Orders.AsNoTracking().CountAsync(Ct));
            host.Log.Clear();

            // Code that names the guard says that it means to save past it.
            using (db.SuppressSaveHandler<Lock>())
                await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(Stages("Push", "Added 'locked'", Every), host.Log.Lines);
        Assert.Equal(1, await host.InScopeAsync(db => db.Orders.CountAsync(Ct)));
    }

    // ── Tell nobody ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task SuppressAfterCommit_tells_nobody_and_still_runs_what_keeps_the_data_right_and_the_callers_own_AfterCommit()
    {
        await using var host = SavePipelineHost.Start(services => services
            .AddSaveHandler<Push>()
            .AddScoped<IShiftDbContextSaveObserver, LoggingSaveObserver>());

        await host.InScopeAsync(async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);

            using (db.SuppressAfterCommit())
            {
                db.Add(new Order { Name = "a" });
                await db.SaveChangesAsync(Ct);

                // Work the code queues itself is the caller's own.
                db.AfterCommit(() => host.Log.Add("the caller's own work"));
            }

            await transaction.CommitAsync(Ct);
        });

        // No part of the framework was told (the observer stands for replication and the notification rules), and no
        // after-commit handler ran. The handlers before the commit did, and so did the work the code queued itself.
        Assert.Equal(
        [
            "Push before save: Added 'a'",
            "Push after save: Added 'a'",
            "Push before commit: Added 'a'",
            "the caller's own work",
        ], host.Log.Lines);
    }

    // ── A switch holds for the save's rows through to the commit ────────────────

    [Fact]
    public async Task A_switch_that_ended_before_the_commit_still_holds_for_the_rows_saved_under_it_and_scopes_nest()
    {
        await using var host = SavePipelineHost.Start(services => services.AddSaveHandler<Push>().AddSaveHandler<Mail>());

        await host.InScopeAsync(async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);

            using (db.SuppressSaveHandler<Push>())
            {
                using (db.SuppressSaveHandlers(SaveStages.BeforeSave | SaveStages.AfterSave))
                {
                    db.Add(new Order { Name = "under both" });
                    await db.SaveChangesAsync(Ct);
                }

                // The inner scope ended: the outer one is still on.
                db.Add(new Order { Name = "under one" });
                await db.SaveChangesAsync(Ct);
            }

            db.Add(new Order { Name = "under none" });
            await db.SaveChangesAsync(Ct);

            host.Log.Add("commit");
            await transaction.CommitAsync(Ct);
        });

        Assert.Equal(
        [
            // under both: no handler before the commit.
            // under one: only Mail.
            "Mail before save: Added 'under one'",
            "Mail after save: Added 'under one'",
            // under none: both.
            "Push before save: Added 'under none'",
            "Mail before save: Added 'under none'",
            "Push after save: Added 'under none'",
            "Mail after save: Added 'under none'",
            "commit",
            // At the commit both scopes have ended, and each handler still gets only the rows of the saves it was on for.
            "Push before commit: Added 'under none'",
            "Mail before commit: Added 'under both', Added 'under one', Added 'under none'",
            "Push after commit: Added 'under none'",
            "Mail after commit: Added 'under both', Added 'under one', Added 'under none'",
        ], host.Log.Lines);
    }

    // ── What a before-save handler can tell ─────────────────────────────────────

    /// <summary>Writes which of its stages a switch keeps the save's rows from, as its before-save stage is told.</summary>
    public sealed class SeesItsSwitches(SaveLog log) : IBeforeSaveHandler<Memo>, IAfterCommitHandler<Memo>
    {
        public ValueTask BeforeSaveAsync(PendingRows<Memo> rows, CancellationToken cancellationToken)
        {
            log.Add($"{rows[0].Entity.Name}: before save {rows.IsSwitchedOff(SaveStages.BeforeSave)}, after save {rows.IsSwitchedOff(SaveStages.AfterSave)}, " +
                $"after commit {rows.IsSwitchedOff(SaveStages.AfterCommit)}");
            return ValueTask.CompletedTask;
        }

        public ValueTask AfterCommitAsync(SavedRows<Memo> rows, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task A_before_save_handler_can_tell_which_of_its_later_stages_a_switch_keeps_the_saves_rows_from()
    {
        await using var host = SavePipelineHost.Start(services => services.AddSaveHandler<SeesItsSwitches>().AddSaveHandler<CommitLog<Memo>>());

        await host.InScopeAsync(async db =>
        {
            async Task SaveAsync(string name)
            {
                db.Add(new Memo { Name = name });
                await db.SaveChangesAsync(Ct);
            }

            await SaveAsync("no switch");

            using (db.SuppressSaveHandlers(SaveStages.AfterCommit))
                await SaveAsync("the after-commit stage");

            using (db.SuppressAfterCommit())
                await SaveAsync("tell nobody");

            using (db.SuppressSaveHandlers(SaveStages.AfterSave | SaveStages.BeforeCommit))
                await SaveAsync("two other stages");

            using (db.SuppressSaveHandler<CommitLog<Memo>>())
                await SaveAsync("another handler named");

            // A set a test builds has no switch.
            Assert.False(HandlerRows.Pending(db, new ChangedRow<Memo>(new Memo(), RowChange.Added)).IsSwitchedOff(SaveStages.AfterCommit));
        });

        Assert.Equal(
        [
            "no switch: before save False, after save False, after commit False",
            "the after-commit stage: before save False, after save False, after commit True",
            "tell nobody: before save False, after save False, after commit True",
            "two other stages: before save False, after save True, after commit False",
            "another handler named: before save False, after save False, after commit False",
        ], host.Log.Lines.Where(line => !line.StartsWith("after commit of")));
    }

    // ── Scopes that end out of order ────────────────────────────────────────────

    [Fact]
    public async Task Switch_scopes_that_end_out_of_order_leave_no_switch_on()
    {
        await using var host = SavePipelineHost.Start(services => services
            .AddSaveHandler<CommitLog<Memo>>()
            .AddSaveHandler<CommitLog<Ticket>>()
            .AddSingleton<IShiftDbContextSaveObserver, LoggingSaveObserver>());

        await host.InScopeAsync(async db =>
        {
            // Each scope takes its own entry out, whichever ends first.
            var first = db.SuppressSaveHandler<CommitLog<Memo>>();
            var second = db.SuppressSaveHandler<CommitLog<Ticket>>();
            var third = db.SuppressAfterCommit();
            var fourth = db.SuppressReplication();

            first.Dispose();
            third.Dispose();
            second.Dispose();
            fourth.Dispose();

            db.Add(new Memo { Name = "after every scope ended" });
            db.Add(new Ticket { Queue = "a", Name = "after every scope ended" });
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(
        [
            "observer: Added Memo, Added Ticket",
            "after commit of Memo: Added Memo 'after every scope ended'",
            "after commit of Ticket: Added Ticket 'after every scope ended'",
        ], host.Log.Lines);
    }

    [Fact]
    public async Task Nested_scopes_of_one_switch_keep_it_on_until_the_last_of_them_ends_whichever_ends_first()
    {
        await using var host = SavePipelineHost.Start(services => services
            .AddSaveHandler<CommitLog<Memo>>()
            .AddSingleton<IShiftDbContextSaveObserver, LoggingSaveObserver>());

        await host.InScopeAsync(async db =>
        {
            async Task SaveAsync(string name)
            {
                db.Add(new Memo { Name = name });
                await db.SaveChangesAsync(Ct);
            }

            // The handler's switch and replication's, each twice. The outer scope ends first.
            var outerHandler = db.SuppressSaveHandler<CommitLog<Memo>>();
            var innerHandler = db.SuppressSaveHandler<CommitLog<Memo>>();
            var outerReplication = db.SuppressReplication();
            var innerReplication = db.SuppressReplication();

            outerHandler.Dispose();
            outerReplication.Dispose();
            await SaveAsync("the inner scopes are still alive");

            // Disposing a scope twice takes out nothing more.
            outerHandler.Dispose();
            outerReplication.Dispose();
            await SaveAsync("still");

            innerHandler.Dispose();
            innerReplication.Dispose();
            await SaveAsync("every scope ended");
        });

        Assert.Equal(
        [
            "observer: Added Memo",
            "after commit of Memo: Added Memo 'every scope ended'",
        ], host.Log.Lines);
    }

    [Fact]
    public async Task A_record_added_under_SuppressSaveHandler_and_modified_without_it_is_a_modified_row_for_that_handler_and_an_added_row_for_another()
    {
        await using var host = SavePipelineHost.Start(services => services.AddSaveHandler<Push>().AddSaveHandler<Mail>());

        await host.InScopeAsync(async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);

            var order = new Order { Name = "a" };
            db.Add(order);

            using (db.SuppressSaveHandler<Push>())
                await db.SaveChangesAsync(Ct);

            order.Name = "b";
            await db.SaveChangesAsync(Ct);

            host.Log.Clear();
            await transaction.CommitAsync(Ct);
        });

        // Net rows are worked out for each handler, over the saves it was not switched off for.
        Assert.Equal(
        [
            "Push before commit: Modified 'b'",
            "Mail before commit: Added 'b'",
            "Push after commit: Modified 'b'",
            "Mail after commit: Added 'b'",
        ], host.Log.Lines);
    }

    [Fact]
    public async Task Rows_saved_with_every_commit_time_handler_switched_off_are_not_kept_for_the_commit()
    {
        await using var host = SavePipelineHost.Start(services => services.AddSaveHandler<CommitLog<Memo>>());

        await host.InScopeAsync(async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);

            // A long import in one transaction: with the handler switched off, the unit does not hold its rows.
            using (db.SuppressSaveHandler<CommitLog<Memo>>())
            {
                for (var batch = 0; batch < 3; batch++)
                {
                    db.AddRange(Enumerable.Range(0, 10).Select(i => new Memo { Name = $"memo {batch}.{i}" }));
                    await db.SaveChangesAsync(Ct);
                    db.ChangeTracker.Clear();
                }
            }

            Assert.Equal(0, db.RowsCollected);
            await transaction.CommitAsync(Ct);
        });

        Assert.Empty(host.Log.Lines);
        Assert.Equal(30, await host.InScopeAsync(db => db.Memos.CountAsync(Ct)));
    }
}
