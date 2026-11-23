using EntityFrameworkCore.Triggered;
using EntityFrameworkCore.Triggered.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.EFCore;
using Xunit;
using static ShiftSoftware.ShiftEntity.Tests.SaveHandlers.SavePipelineModel;

namespace ShiftSoftware.ShiftEntity.Tests.SaveHandlers;

/// <summary>
/// Save handlers (<see cref="ISaveHandler{TEntity}"/>): the host's own code that runs after a save of a
/// <see cref="ShiftDbContext"/> has written a row, in place of an after-save trigger of a trigger library. These pin
/// when a handler runs, what it is given, and what it may do: save again, fail, or be skipped.
/// </summary>
public class SaveHandlerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── Which rows ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_handler_runs_for_each_row_of_its_type_that_a_save_added_modified_or_deleted()
    {
        await using var host = SavePipelineHost.Start(services => services.AddSaveHandler<Order, LoggingSaveHandler<Order>>());

        await host.InScopeAsync(async db =>
        {
            var order = new Order { Name = "first" };
            db.Add(order);
            db.Add(new Memo { Name = "not an order" });
            await db.SaveChangesAsync(Ct);

            order.Name = "renamed";
            await db.SaveChangesAsync(Ct);

            db.Remove(order);
            await db.SaveChangesAsync(Ct);

            await db.SaveChangesAsync(Ct);      // a save that writes nothing runs nothing
        });

        Assert.Equal(
            ["Order handler: Added Order 'first'", "Order handler: Modified Order 'renamed'", "Order handler: Deleted Order 'renamed'"],
            host.Log.Lines);
    }

    [Fact]
    public async Task A_handler_for_a_base_class_or_an_interface_runs_for_every_row_that_is_one()
    {
        await using var host = SavePipelineHost.Start(services => services
            .AddSaveHandler<Order, LoggingSaveHandler<Order>>()
            .AddSaveHandler<INamed, LoggingSaveHandler<INamed>>());

        await host.InScopeAsync(async db =>
        {
            db.Add(new RushOrder { Name = "rush" });
            await db.SaveChangesAsync(Ct);

            db.Add(new Memo { Name = "memo" });
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(
            ["Order handler: Added RushOrder 'rush'", "INamed handler: Added RushOrder 'rush'", "INamed handler: Added Memo 'memo'"],
            host.Log.Lines);
    }

    [Fact]
    public async Task Handlers_of_one_row_run_in_the_order_they_were_registered_and_the_same_one_twice_runs_once()
    {
        await using var host = SavePipelineHost.Start(services => services
            .AddSaveHandler<INamed, LoggingSaveHandler<INamed>>()
            .AddSaveHandler<Order, LoggingSaveHandler<Order>>()
            .AddSaveHandler<INamed, LoggingSaveHandler<INamed>>());

        await host.InScopeAsync(async db =>
        {
            db.Add(new Order { Name = "one" });
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(["INamed handler: Added Order 'one'", "Order handler: Added Order 'one'"], host.Log.Lines);
    }

    // ── When ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Handlers_run_after_every_other_observer_whatever_the_order_of_registration()
    {
        await using var host = SavePipelineHost.Start(services => services
            .AddSaveHandler<Memo, LoggingSaveHandler<Memo>>()
            .AddScoped<IShiftDbContextSaveObserver, LoggingSaveObserver>());

        await host.InScopeAsync(async db =>
        {
            db.Add(new Memo { Name = "one" });
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(["observer: Added Memo", "Memo handler: Added Memo 'one'"], host.Log.Lines);
    }

    public sealed class CountedHandler : ISaveHandler<Order>
    {
        public static int Built;

        public CountedHandler() => Interlocked.Increment(ref Built);

        public ValueTask HandleAsync(SavedRow<Order> row, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task A_handler_is_built_only_when_a_row_of_its_type_was_saved()
    {
        await using var host = SavePipelineHost.Start(services => services.AddSaveHandler<Order, CountedHandler>());
        CountedHandler.Built = 0;

        await host.InScopeAsync(async db =>
        {
            db.Add(new Memo { Name = "memo" });
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(0, CountedHandler.Built);

        await host.InScopeAsync(async db =>
        {
            db.Add(new Order { Name = "one" });
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(1, CountedHandler.Built);
    }

    public sealed class RecordsWhenTheWorkRan(SaveLog log) : ISaveHandler<Order>
    {
        public ValueTask HandleAsync(SavedRow<Order> row, CancellationToken cancellationToken)
        {
            log.Add($"handler ran for '{row.Entity.Name}'");
            row.Context.AfterCommit(() => log.Add($"after commit of '{row.Entity.Name}'"));
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task A_handler_runs_inside_the_callers_transaction_and_its_after_commit_work_waits_for_the_commit()
    {
        await using var host = SavePipelineHost.Start(
            services => services.AddSaveHandler<Order, RecordsWhenTheWorkRan>(),
            context => context.UseShiftEntityAfterCommit());

        await host.InScopeAsync(async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);

            db.Add(new Order { Name = "kept" });
            await db.SaveChangesAsync(Ct);
            host.Log.Add("save returned");

            await transaction.CommitAsync(Ct);
        });

        await host.InScopeAsync(async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);

            db.Add(new Order { Name = "dropped" });
            await db.SaveChangesAsync(Ct);

            await transaction.RollbackAsync(Ct);
        });

        Assert.Equal(
            ["handler ran for 'kept'", "save returned", "after commit of 'kept'", "handler ran for 'dropped'"],
            host.Log.Lines);
    }

    // ── Values from before the save ─────────────────────────────────────────────

    [Fact]
    public async Task A_handler_that_asked_for_them_gets_a_modified_rows_values_from_before_the_save()
    {
        await using var host = SavePipelineHost.Start(services => services.AddSaveHandler<Order, OrderBeforeAndAfter>(withOriginal: true));

        var id = await host.InScopeAsync(async db =>
        {
            var order = new Order { Name = "draft", Status = "New" };
            db.Add(order);
            await db.SaveChangesAsync(Ct);
            return order.ID;
        });

        await host.InScopeAsync(async db =>
        {
            var order = await db.Orders.SingleAsync(x => x.ID == id, Ct);
            order.Status = "Resolved";
            await db.SaveChangesAsync(Ct);

            db.Remove(order);
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(
            ["Added: ''/'' -> 'draft'/'New'", "Modified: 'draft'/'New' -> 'draft'/'Resolved'", "Deleted: ''/'' -> 'draft'/'Resolved'"],
            host.Log.Lines);
    }

    public sealed class ReadsOriginalWithoutAsking(SaveLog log) : ISaveHandler<Order>
    {
        public ValueTask HandleAsync(SavedRow<Order> row, CancellationToken cancellationToken)
        {
            try
            {
                log.Add($"original: '{row.Original?.Name}'");
            }
            catch (InvalidOperationException exception)
            {
                log.Add("refused: " + exception.Message);
            }

            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task A_handler_that_did_not_ask_is_told_so_instead_of_reading_nothing()
    {
        await using var host = SavePipelineHost.Start(services => services.AddSaveHandler<Order, ReadsOriginalWithoutAsking>());

        await host.InScopeAsync(async db =>
        {
            var order = new Order { Name = "draft" };
            db.Add(order);
            await db.SaveChangesAsync(Ct);

            order.Name = "renamed";
            await db.SaveChangesAsync(Ct);
        });

        Assert.All(host.Log.Lines, line => Assert.Contains("withOriginal: true", line));
        Assert.Equal(2, host.Log.Lines.Count);
    }

    [Fact]
    public async Task A_row_built_by_a_test_gives_the_original_it_was_built_with()
    {
        await using var host = SavePipelineHost.Start();

        await host.InScopeAsync(db =>
        {
            var row = new SavedRow<Order>
            {
                Entity = new Order { Name = "after" },
                Original = new Order { Name = "before" },
                Change = SavedRowChange.Modified,
                Context = db,
            };

            Assert.Equal("before", row.Original?.Name);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task A_change_that_only_the_saves_own_detection_finds_still_comes_with_the_old_values()
    {
        // Nothing has detected the edit when the save starts: the audit backfill, which would, is switched off, as it
        // is for a repository's save after its before-save hooks have run.
        await using var host = SavePipelineHost.Start(services => services.AddSaveHandler<Order, OrderBeforeAndAfter>(withOriginal: true));

        var id = await host.InScopeAsync(async db =>
        {
            var order = new Order { Name = "draft", Status = "New" };
            db.Add(order);
            await db.SaveChangesAsync(Ct);
            return order.ID;
        });

        host.Log.Clear();

        await host.InScopeAsync(async db =>
        {
            var order = await db.Orders.SingleAsync(x => x.ID == id, Ct);
            order.Status = "Resolved";

            using (db.SuppressAuditStamping())
                await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(["Modified: 'draft'/'New' -> 'draft'/'Resolved'"], host.Log.Lines);
    }

    public sealed class ResolveTrackedOrdersBeforeSave(Db db) : IBeforeSaveTrigger<Memo>
    {
        public Task BeforeSave(ITriggerContext<Memo> context, CancellationToken cancellationToken)
        {
            foreach (var order in db.Orders.Local.ToList())
                order.Status = "Resolved";

            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task A_row_that_a_trigger_librarys_before_save_step_changes_comes_with_the_old_values()
    {
        await using var host = SavePipelineHost.Start(
            services => services
                .AddSaveHandler<Order, OrderBeforeAndAfter>(withOriginal: true)
                .AddTransient<IBeforeSaveTrigger<Memo>, ResolveTrackedOrdersBeforeSave>(),
            context => context.UseTriggers());

        var id = await host.InScopeAsync(async db =>
        {
            var order = new Order { Name = "draft", Status = "New" };
            db.Add(order);
            await db.SaveChangesAsync(Ct);
            return order.ID;
        });

        host.Log.Clear();

        await host.InScopeAsync(async db =>
        {
            await db.Orders.SingleAsync(x => x.ID == id, Ct);   // tracked, unchanged when the save starts
            db.Add(new Memo { Name = "its before-save step resolves the order" });
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(["Modified: 'draft'/'New' -> 'draft'/'Resolved'"], host.Log.Lines);
    }

    [Fact]
    public async Task An_order_whose_owned_value_alone_changed_is_a_modified_row_with_its_old_values()
    {
        // EF writes the owner's row when only an owned value changes, and marks the owner modified while it prepares
        // the write, after the save has started.
        await using var host = SavePipelineHost.Start(services => services.AddSaveHandler<Order, OrderBeforeAndAfter>(withOriginal: true));

        var id = await host.InScopeAsync(async db =>
        {
            var order = new Order { Name = "draft", Status = "New", Address = new Address { City = "one" } };
            db.Add(order);
            await db.SaveChangesAsync(Ct);
            return order.ID;
        });

        host.Log.Clear();

        await host.InScopeAsync(async db =>
        {
            var order = await db.Orders.SingleAsync(x => x.ID == id, Ct);
            order.Address.City = "another";
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(["Modified: 'draft'/'New' -> 'draft'/'New'"], host.Log.Lines);
    }

    [Fact]
    public async Task Keeping_old_values_runs_no_change_detection_of_its_own()
    {
        // Looking for the modified lines used to detect changes line by line, and each line's detection also detected
        // the order it belongs to: with N tracked lines the order was detected N + 1 times in one save. Now each row is
        // detected as often as without the handler: by the audit backfill and by EF's own save.
        const int lines = 200;

        async Task<(int Order, int Lines)> DetectionsInOneSaveAsync(Action<IServiceCollection>? services)
        {
            await using var host = SavePipelineHost.Start(services);

            await host.InScopeAsync(async db =>
            {
                var order = new Order { Name = "order" };
                for (var i = 0; i < lines; i++)
                    order.Lines.Add(new OrderLine { Note = "line" });
                db.Add(order);
                await db.SaveChangesAsync(Ct);
            });

            return await host.InScopeAsync(async db =>
            {
                var order = await db.Orders.Include(x => x.Lines).SingleAsync(Ct);
                order.Lines[0].Note = "edited";

                int orderDetections = 0, lineDetections = 0;
                db.ChangeTracker.DetectingEntityChanges += (_, e) =>
                {
                    if (e.Entry.Entity is Order)
                        orderDetections++;
                    else if (e.Entry.Entity is OrderLine)
                        lineDetections++;
                };

                await db.SaveChangesAsync(Ct);
                return (orderDetections, lineDetections);
            });
        }

        var without = await DetectionsInOneSaveAsync(null);
        var with = await DetectionsInOneSaveAsync(services => services.AddSaveHandler<OrderLine, LineHandler>(withOriginal: true));

        Assert.Equal(without, with);
        Assert.Equal(2, with.Order);
    }

    public sealed class LineHandler : ISaveHandler<OrderLine>
    {
        public ValueTask HandleAsync(SavedRow<OrderLine> row, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    // ── Saving again ────────────────────────────────────────────────────────────

    public sealed class WritesAMemoForEachNewOrder(Db db, SaveLog log) : ISaveHandler<Order>
    {
        public async ValueTask HandleAsync(SavedRow<Order> row, CancellationToken cancellationToken)
        {
            log.Add($"order handler: {row.Change} '{row.Entity.Name}'");

            if (row.Change != SavedRowChange.Added)
                return;

            db.Add(new Memo { Name = "memo for " + row.Entity.Name });
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task A_handler_that_saves_again_writes_only_its_own_rows_and_that_save_runs_its_own_handlers()
    {
        // Three new orders in one save, and a handler that saves a memo for each. Every order is already written and
        // accepted when the first handler runs, so its save writes the memo and nothing else.
        await using var host = SavePipelineHost.Start(services => services
            .AddSaveHandler<Order, WritesAMemoForEachNewOrder>()
            .AddSaveHandler<Memo, LoggingSaveHandler<Memo>>());

        await host.InScopeAsync(async db =>
        {
            db.AddRange(new Order { Name = "a" }, new Order { Name = "b" }, new Order { Name = "c" });
            await db.SaveChangesAsync(Ct);
        });

        await host.InScopeAsync(async db =>
        {
            Assert.Equal(["a", "b", "c"], await db.Orders.OrderBy(x => x.ID).Select(x => x.Name).ToListAsync(Ct));
            Assert.Equal(3, await db.Memos.CountAsync(Ct));
        });

        Assert.Equal(
        [
            "order handler: Added 'a'", "Memo handler: Added Memo 'memo for a'",
            "order handler: Added 'b'", "Memo handler: Added Memo 'memo for b'",
            "order handler: Added 'c'", "Memo handler: Added Memo 'memo for c'",
        ], host.Log.Lines);
    }

    public sealed class StoresItsOutcomeOnTheOrder(Db db, SaveLog log, StoresItsOutcomeOnTheOrder.Settings settings) : ISaveHandler<Order>
    {
        public sealed class Settings
        {
            public bool SaveWithoutHandlers { get; set; }
        }

        public async ValueTask HandleAsync(SavedRow<Order> row, CancellationToken cancellationToken)
        {
            log.Add($"pushed '{row.Entity.Name}' while its status was '{row.Entity.Status}'");

            // As a push to another system that keeps its outcome on the record does.
            if (row.Entity.Status == "Refused")
                return;

            row.Entity.Status = "Refused";

            if (settings.SaveWithoutHandlers)
                await db.SaveChangesWithoutHandlersAsync(cancellationToken);
            else
                await db.SaveChangesAsync(cancellationToken);
        }
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    public async Task A_handler_that_saves_its_own_row_runs_again_for_that_save_unless_it_saves_without_handlers(bool withoutHandlers, int runs)
    {
        await using var host = SavePipelineHost.Start(services => services
            .AddSingleton(new StoresItsOutcomeOnTheOrder.Settings { SaveWithoutHandlers = withoutHandlers })
            .AddSaveHandler<Order, StoresItsOutcomeOnTheOrder>());

        await host.InScopeAsync(async db =>
        {
            db.Add(new Order { Name = "lead", Status = "New" });
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(runs, host.Log.Lines.Count);
        Assert.Equal("Refused", await host.InScopeAsync(db => db.Orders.Select(x => x.Status).SingleAsync(Ct)));
    }

    // ── Skipping ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_save_without_handlers_runs_none_and_is_still_reported_to_the_observers()
    {
        await using var host = SavePipelineHost.Start(services => services
            .AddSaveHandler<Memo, LoggingSaveHandler<Memo>>()
            .AddScoped<IShiftDbContextSaveObserver, LoggingSaveObserver>());

        await host.InScopeAsync(async db =>
        {
            db.Add(new Memo { Name = "async" });
            await db.SaveChangesWithoutHandlersAsync(Ct);

            db.Add(new Memo { Name = "sync" });
            db.SaveChangesWithoutHandlers();

            using (db.SuppressSaveHandlers())
            {
                db.Add(new Memo { Name = "scope" });
                await db.SaveChangesAsync(Ct);
            }

            db.Add(new Memo { Name = "after" });
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(
            ["observer: Added Memo", "observer: Added Memo", "observer: Added Memo", "observer: Added Memo", "Memo handler: Added Memo 'after'"],
            host.Log.Lines);
    }

    [Fact]
    public async Task A_save_under_SuppressReplication_still_runs_the_handlers_and_reports_to_no_other_observer()
    {
        await using var host = SavePipelineHost.Start(services => services
            .AddSaveHandler<Order, LoggingSaveHandler<Order>>()
            .AddScoped<IShiftDbContextSaveObserver, LoggingSaveObserver>());

        await host.InScopeAsync(async db =>
        {
            using (db.SuppressReplication())
            {
                db.Add(new Order { Name = "seeded" });
                await db.SaveChangesAsync(Ct);
            }
        });

        Assert.Equal(["Order handler: Added Order 'seeded'"], host.Log.Lines);
    }

    [Fact]
    public async Task Each_switch_silences_only_its_own_observers_and_the_three_together_silence_every_one()
    {
        await using var host = SavePipelineHost.Start(services => services
            .AddSaveHandler<Memo, LoggingSaveHandler<Memo>>()
            .AddScoped<IShiftDbContextSaveObserver, LoggingSaveObserver>());

        await host.InScopeAsync(async db =>
        {
            using (db.SuppressNotifications())
            {
                db.Add(new Memo { Name = "without notifications" });
                await db.SaveChangesAsync(Ct);
            }

            using (db.SuppressReplication())
            using (db.SuppressNotifications())
            using (db.SuppressSaveHandlers())
            {
                db.Add(new Memo { Name = "silent" });
                await db.SaveChangesAsync(Ct);
            }
        });

        Assert.Equal(["observer: Added Memo", "Memo handler: Added Memo 'without notifications'"], host.Log.Lines);
    }

    [Fact]
    public async Task A_save_that_does_not_accept_its_changes_runs_no_handler()
    {
        await using var host = SavePipelineHost.Start(services => services.AddSaveHandler<Order, LoggingSaveHandler<Order>>());

        await host.InScopeAsync(async db =>
        {
            db.Add(new Order { Name = "one" });
            await db.SaveChangesAsync(acceptAllChangesOnSuccess: false, Ct);
            db.ChangeTracker.AcceptAllChanges();
        });

        Assert.Empty(host.Log.Lines);
    }

    [Fact]
    public async Task The_synchronous_save_runs_the_handlers_before_it_returns()
    {
        await using var host = SavePipelineHost.Start(services => services.AddSaveHandler<Order, LoggingSaveHandler<Order>>());

        await host.InScopeAsync(db =>
        {
            db.Add(new Order { Name = "sync" });
            db.SaveChanges();
            host.Log.Add("save returned");
            return Task.CompletedTask;
        });

        Assert.Equal(["Order handler: Added Order 'sync'", "save returned"], host.Log.Lines);
    }

    // ── Next to a trigger library ───────────────────────────────────────────────

    public sealed class LoggingAfterSaveTrigger(SaveLog log) : IAfterSaveTrigger<Memo>
    {
        public Task AfterSave(ITriggerContext<Memo> context, CancellationToken cancellationToken)
        {
            log.Add($"trigger: {context.ChangeType} '{context.Entity.Name}'");
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task A_host_that_still_has_triggers_runs_them_first_and_each_kind_has_its_own_switch()
    {
        // A host moving off a trigger library has both for a while. The library's after-save step runs inside EF's
        // save, so before the handlers, and neither switch knows about the other.
        await using var host = SavePipelineHost.Start(
            services => services
                .AddSaveHandler<Memo, LoggingSaveHandler<Memo>>()
                .AddTransient<IAfterSaveTrigger<Memo>, LoggingAfterSaveTrigger>(),
            context => context.UseTriggers());

        await host.InScopeAsync(async db =>
        {
            db.Add(new Memo { Name = "both" });
            await db.SaveChangesAsync(Ct);

            db.Add(new Memo { Name = "no triggers" });
            await db.SaveChangesWithoutTriggersAsync(Ct);

            db.Add(new Memo { Name = "no handlers" });
            await db.SaveChangesWithoutHandlersAsync(Ct);
        });

        Assert.Equal(
        [
            "trigger: Added 'both'", "Memo handler: Added Memo 'both'",
            "Memo handler: Added Memo 'no triggers'",
            "trigger: Added 'no handlers'",
        ], host.Log.Lines);
    }

    // ── Failing ─────────────────────────────────────────────────────────────────

    public sealed class FailsOnTheFirstOrder(SaveLog log) : ISaveHandler<Order>
    {
        public ValueTask HandleAsync(SavedRow<Order> row, CancellationToken cancellationToken)
        {
            log.Add($"handler: '{row.Entity.Name}'");

            if (row.Entity.Name == "a")
                throw new InvalidOperationException("The handler failed.");

            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task A_handler_that_throws_fails_the_save_call_and_leaves_every_row_saved_once()
    {
        // Host code used to run while EF was still accepting the rows: a failure left the later rows marked as new,
        // and saving again wrote them a second time. A handler runs once EF has finished, so nothing is left half done.
        await using var host = SavePipelineHost.Start(services => services.AddSaveHandler<Order, FailsOnTheFirstOrder>());

        await host.InScopeAsync(async db =>
        {
            var a = new Order { Name = "a" };
            var b = new Order { Name = "b" };
            db.AddRange(a, b);

            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
            Assert.Equal("The handler failed.", failure.Message);

            Assert.Equal(EntityState.Unchanged, db.Entry(a).State);
            Assert.Equal(EntityState.Unchanged, db.Entry(b).State);
            Assert.True(a.ID > 0 && b.ID > 0);

            Assert.Equal(0, await db.SaveChangesAsync(Ct));     // the caller tries again: nothing is left to write
        });

        Assert.Equal(2, await host.InScopeAsync(db => db.Orders.CountAsync(Ct)));

        // The failure stopped the handlers that were still waiting.
        Assert.Equal(["handler: 'a'"], host.Log.Lines);
    }
}
