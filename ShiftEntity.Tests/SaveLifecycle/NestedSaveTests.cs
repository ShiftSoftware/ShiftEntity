using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.Tests.SaveHandlers;
using Xunit;
using static ShiftSoftware.ShiftEntity.Tests.SaveHandlers.SavePipelineModel;
using static ShiftSoftware.ShiftEntity.Tests.SaveLifecycle.ScriptedHandlers;

namespace ShiftSoftware.ShiftEntity.Tests.SaveLifecycle;

/// <summary>
/// Saving from inside a stage. After save and before commit, a save joins the transaction and what commits with it.
/// After commit it is a new save that commits on its own, and ends before the outer stage carries on. A handler is
/// never called again for a record it is handling, and saves nest 16 deep at most.
/// </summary>
public class NestedSaveTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── One at a time ───────────────────────────────────────────────────────────

    [Fact]
    public async Task A_handler_is_called_once_with_all_five_rows_and_may_query_the_context_for_each()
    {
        var calls = 0;

        await using var host = SavePipelineHost.Start(services => services.AddScript(new Script<Order>
        {
            AfterSave = async (rows, ct) =>
            {
                calls++;

                // Nothing else runs on the context meanwhile, so one query after another is safe.
                foreach (var row in rows)
                    await rows.Db.Set<OrderLine>().Where(line => line.OrderID == row.Entity.ID).ToListAsync(ct);
            },
        }));

        await host.InScopeAsync(async db =>
        {
            db.AddRange(Enumerable.Range(1, 5).Select(i => new Order { Name = "order " + i }));
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(1, calls);
        Assert.Equal(5, host.Sql.Lines.Count(line => line == "SELECT"));
    }

    // ── After save and before commit: the same transaction, the same unit ──────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_save_made_after_save_joins_the_transaction_runs_its_own_after_save_and_shares_the_commit(bool sqlServer)
    {
        await using var host = await SavePipelineHost.StartAsync(sqlServer, "NestedAfterSave", services => services
            .AddScript(new Script<Order>
            {
                AfterSave = async (rows, ct) =>
                {
                    rows.Db.Add(new Memo { Name = $"memo of {rows.Count} orders" });
                    await rows.Db.SaveChangesAsync(ct);
                },
            })
            .AddSaveHandler<StageLog<Memo>>());

        await host.InScopeAsync(async db =>
        {
            db.AddRange(new Order { Name = "a" }, new Order { Name = "b" });
            Assert.Equal(2, await db.SaveChangesAsync(Ct));      // the count of the caller's own rows
        });

        Assert.Equal(
        [
            "after save of Order: Added Order 'a', Added Order 'b'",
            "after save of Memo: Added Memo 'memo of 2 orders'",
            "before commit of Memo: Added Memo 'memo of 2 orders'",
            "after commit of Memo: Added Memo 'memo of 2 orders'",
        ], host.Log.Lines);

        // One transaction for both saves.
        Assert.Single(host.Sql.Writes, line => line == "BEGIN");
        Assert.Single(host.Sql.Writes, line => line == "COMMIT");
        Assert.Equal(1, await host.InScopeAsync(db => db.Memos.CountAsync(Ct)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_save_made_after_save_that_fails_is_undone_alone_and_the_save_around_it_goes_on(bool sqlServer)
    {
        await using var host = await SavePipelineHost.StartAsync(sqlServer, "NestedFailure", services => services
            .AddScript(new Script<Order>
            {
                AfterSave = async (rows, ct) =>
                {
                    var memo = new Memo { Name = "refused" };
                    rows.Db.Add(memo);

                    try
                    {
                        await rows.Db.SaveChangesAsync(ct);
                    }
                    catch (InvalidOperationException)
                    {
                        // The handler can live without the memo. It stops tracking the row that was not saved after all.
                        rows.Db.Entry(memo).State = EntityState.Detached;
                    }
                },
                AfterCommit = Nothing,
            })
            .AddScript(new Script<Memo>
            {
                AfterSave = (_, _) => throw new InvalidOperationException("No memos today."),
                AfterCommit = Nothing,
            }));

        await host.InScopeAsync(async db =>
        {
            db.Add(new Order { Name = "a" });
            await db.SaveChangesAsync(Ct);

            // The context is not left refusing: the row of the undone save is no longer tracked.
            db.Add(new Order { Name = "b" });
            await db.SaveChangesAsync(Ct);
        });

        // Each save rolled back to the savepoint of the save inside it, and committed.
        Assert.Equal(2, host.Sql.Writes.Count(line => line == "ROLLBACK TO SAVEPOINT"));
        Assert.Equal(2, host.Sql.Writes.Count(line => line == "COMMIT"));
        Assert.DoesNotContain("ROLLBACK", host.Sql.Writes);

        Assert.Equal(
        [
            "after save of Order: Added Order 'a'", "after save of Memo: Added Memo 'refused'", "after commit of Order: Added Order 'a'",
            "after save of Order: Added Order 'b'", "after save of Memo: Added Memo 'refused'", "after commit of Order: Added Order 'b'",
        ], host.Log.Lines);

        await using var bare = host.Bare();
        Assert.Equal((2, 0), (await bare.Orders.CountAsync(Ct), await bare.Memos.CountAsync(Ct)));
    }

    [Fact]
    public async Task An_after_save_handler_that_saves_its_own_record_is_not_called_again_and_the_commit_sees_the_record_once()
    {
        await using var host = SavePipelineHost.Start(services => services
            .AddScript(new Script<Order>
            {
                AfterSave = async (rows, ct) =>
                {
                    // As a number that is taken under a lock once the row exists.
                    foreach (var row in rows)
                        row.Entity.Status = "Numbered " + row.Entity.ID;

                    await rows.Db.SaveChangesAsync(ct);
                },
            })
            .AddSaveHandler<StageLog<INamed>>());

        await host.InScopeAsync(async db =>
        {
            db.Add(new Order { Name = "a" });
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(
        [
            // The outer save: this handler, then the other.
            "after save of Order: Added Order 'a'",

            // Its own save: the other handler is called for the record again, and this one is not.
            "after save of INamed: Modified Order 'a'",
            "after save of INamed: Added Order 'a'",

            // Added and then modified in one transaction is one added row.
            "before commit of INamed: Added Order 'a'",
            "after commit of INamed: Added Order 'a'",
        ], host.Log.Lines);

        Assert.Equal("Numbered 1", await host.InScopeAsync(db => db.Orders.Select(x => x.Status).SingleAsync(Ct)));
    }

    [Fact]
    public async Task A_save_made_before_commit_joins_the_transaction_and_its_rows_get_a_round_of_their_own()
    {
        await using var host = SavePipelineHost.Start(services => services
            .AddScript(new Script<OrderLine>
            {
                BeforeCommit = async (rows, ct) =>
                {
                    // Once per transaction, for every order whose lines it touched: the total is kept on the order.
                    var db = (Db)rows.Db;
                    var ids = rows.Select(row => row.Entity.OrderID).Distinct().ToList();

                    foreach (var order in await db.Orders.Include(x => x.Lines).Where(x => ids.Contains(x.ID)).ToListAsync(ct))
                        order.Status = order.Lines.Count + " line(s)";

                    await db.SaveChangesAsync(ct);
                },
            })
            .AddScript(new Script<Order> { BeforeCommit = Nothing, AfterCommit = Nothing }));

        await host.InScopeAsync(async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);

            var order = new Order { Name = "a" };
            db.Add(order);
            await db.SaveChangesAsync(Ct);

            order.Lines.Add(new OrderLine { Note = "one" });
            await db.SaveChangesAsync(Ct);

            order.Lines.Add(new OrderLine { Note = "two" });
            await db.SaveChangesAsync(Ct);

            host.Log.Add("commit");
            await transaction.CommitAsync(Ct);
        });

        Assert.Equal(
        [
            "commit",

            // The first round: what the transaction saved, one row per record.
            "before commit of OrderLine: Added OrderLine '', Added OrderLine ''",
            "before commit of Order: Added Order 'a'",

            // The second round: only what the first round saved.
            "before commit of Order: Modified Order 'a'",

            "after commit of Order: Added Order 'a'",
        ], host.Log.Lines);

        Assert.Equal("2 line(s)", await host.InScopeAsync(db => db.Orders.Select(x => x.Status).SingleAsync(Ct)));
    }

    [Fact]
    public async Task A_before_commit_handler_that_saves_its_own_record_is_not_called_for_it_in_the_next_round()
    {
        await using var host = SavePipelineHost.Start(services => services
            .AddScript(new Script<Order>
            {
                BeforeCommit = async (rows, ct) =>
                {
                    foreach (var row in rows)
                        row.Entity.Status = "Checked";

                    await rows.Db.SaveChangesAsync(ct);
                },
            })
            .AddSaveHandler<StageLog<INamed>>());

        await host.InScopeAsync(async db =>
        {
            db.Add(new Order { Name = "a" });
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(
        [
            "after save of INamed: Added Order 'a'",
            "before commit of Order: Added Order 'a'",
            "after save of INamed: Modified Order 'a'",
            "before commit of INamed: Added Order 'a'",

            // The next round has the record that the first handler saved. The other handler gets it, the first doesn't.
            "before commit of INamed: Modified Order 'a'",
            "after commit of INamed: Added Order 'a'",
        ], host.Log.Lines);
    }

    [Fact]
    public async Task Before_commit_handlers_that_keep_saving_new_rows_are_stopped_after_16_rounds_and_named()
    {
        await using var host = SavePipelineHost.Start(services => services.AddScript(new Script<Memo>
        {
            BeforeCommit = async (rows, ct) =>
            {
                rows.Db.Add(new Memo { Name = "one more" });
                await rows.Db.SaveChangesAsync(ct);
            },
        }));

        await host.InScopeAsync(async db =>
        {
            db.Add(new Memo { Name = "first" });

            var loop = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
            Assert.Contains("after 16 rounds", loop.Message);
            Assert.Contains("OnBeforeCommit`1 (before commit, Memo)", loop.Message);
        });

        Assert.Equal(16, host.Log.Lines.Count);
        Assert.Equal(0, await host.InScopeAsync(db => db.Memos.CountAsync(Ct)));
    }

    // ── After commit: a new save, with a commit of its own ──────────────────────

    [Fact]
    public async Task A_save_made_after_commit_commits_on_its_own_and_ends_before_the_next_handler_of_the_outer_commit()
    {
        await using var host = SavePipelineHost.Start(services => services
            .AddScript(new Script<Order>
            {
                AfterCommit = async (rows, ct) =>
                {
                    rows.Db.Add(new Memo { Name = "memo of " + rows[0].Entity.Name });
                    await rows.Db.SaveChangesAsync(ct);
                    rows.Services.GetRequiredService<SaveLog>().Add("the order's handler carries on");
                },
            })
            .AddSaveHandler<CommitLog<Memo>>()
            .AddSaveHandler<CommitLog<INamed>>());

        await host.InScopeAsync(async db =>
        {
            db.Add(new Order { Name = "a" });
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(
        [
            "after commit of Order: Added Order 'a'",
            "after commit of Memo: Added Memo 'memo of a'",
            "after commit of INamed: Added Memo 'memo of a'",
            "the order's handler carries on",
            "after commit of INamed: Added Order 'a'",
        ], host.Log.Lines);
    }

    // ── A handler is not called again for a record it is handling ───────────────

    public sealed class StoresItsOutcome(SaveLog log) : IAfterCommitHandler<Order>
    {
        public async ValueTask AfterCommitAsync(SavedRows<Order> rows, CancellationToken cancellationToken)
        {
            foreach (var row in rows)
            {
                log.Add($"handling '{row.Entity.Name}' while its status is '{row.Entity.Status}'");
                row.Entity.Status = "Sent";
            }

            await rows.Db.SaveChangesAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task An_after_commit_handler_that_saves_its_record_again_is_not_called_again_and_a_second_handler_is()
    {
        await using var host = SavePipelineHost.Start(services => services
            .AddSaveHandler<StoresItsOutcome>()
            .AddSaveHandler<CommitLog<INamed>>());

        await host.InScopeAsync(async db =>
        {
            db.AddRange(new Order { Name = "a" }, new Order { Name = "b" });
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(
        [
            "handling 'a' while its status is ''",
            "handling 'b' while its status is ''",

            // The handler's own save, which ends before the outer commit's next handler starts.
            "after commit of INamed: Modified Order 'a', Modified Order 'b'",
            "after commit of INamed: Added Order 'a', Added Order 'b'",
        ], host.Log.Lines);

        Assert.Equal(["Sent", "Sent"], await host.InScopeAsync(db => db.Orders.Select(x => x.Status).ToListAsync(Ct)));
    }

    public sealed class SavesAnotherRecord(SaveLog log) : IAfterCommitHandler<Order>
    {
        public async ValueTask AfterCommitAsync(SavedRows<Order> rows, CancellationToken cancellationToken)
        {
            log.Add("handling " + string.Join(", ", rows.Select(row => $"'{row.Entity.Name}'")));

            if (rows[0].Entity.Name != "first")
                return;

            // Its own record, and another record of the same type.
            rows[0].Entity.Status = "Sent";
            rows.Db.Add(new Order { Name = "second" });
            await rows.Db.SaveChangesAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task A_handler_that_saves_another_record_of_its_type_is_called_for_that_one()
    {
        await using var host = SavePipelineHost.Start(services => services.AddSaveHandler<SavesAnotherRecord>());

        await host.InScopeAsync(async db =>
        {
            db.Add(new Order { Name = "first" });
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(["handling 'first'", "handling 'second'"], host.Log.Lines);
    }

    public sealed class ReloadsAndSaves(SaveLog log) : IAfterCommitHandler<Order>
    {
        public async ValueTask AfterCommitAsync(SavedRows<Order> rows, CancellationToken cancellationToken)
        {
            var db = (Db)rows.Db;
            var id = rows[0].Entity.ID;
            log.Add($"handling order {id}");

            // The same record as another instance: the tracker is cleared and the record loaded again.
            db.ChangeTracker.Clear();
            var loaded = await db.Orders.SingleAsync(x => x.ID == id, cancellationToken);
            log.Add($"the same instance: {ReferenceEquals(loaded, rows[0].Entity)}");

            loaded.Status = "Sent";
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task A_record_is_the_same_record_when_the_handler_loaded_it_again_as_another_instance()
    {
        await using var host = SavePipelineHost.Start(services => services.AddSaveHandler<ReloadsAndSaves>());

        await host.InScopeAsync(async db =>
        {
            db.Add(new Order { Name = "a" });
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(["handling order 1", "the same instance: False"], host.Log.Lines);
        Assert.Equal("Sent", await host.InScopeAsync(db => db.Orders.Select(x => x.Status).SingleAsync(Ct)));
    }

    public sealed class OrdersTouchTheirMemo(SaveLog log) : IAfterCommitHandler<Order>
    {
        public async ValueTask AfterCommitAsync(SavedRows<Order> rows, CancellationToken cancellationToken)
        {
            log.Add("the order's handler");
            var memo = await ((Db)rows.Db).Memos.SingleAsync(cancellationToken);
            memo.Name += " +order";
            await rows.Db.SaveChangesAsync(cancellationToken);
        }
    }

    public sealed class MemosTouchTheirOrder(SaveLog log) : IAfterCommitHandler<Memo>
    {
        public async ValueTask AfterCommitAsync(SavedRows<Memo> rows, CancellationToken cancellationToken)
        {
            log.Add("the memo's handler");
            var order = await ((Db)rows.Db).Orders.SingleAsync(cancellationToken);
            order.Status += " +memo";
            await rows.Db.SaveChangesAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task Two_handlers_that_write_each_others_records_stop()
    {
        await using var host = SavePipelineHost.Start(services => services
            .AddSaveHandler<OrdersTouchTheirMemo>()
            .AddSaveHandler<MemosTouchTheirOrder>());

        await host.InScopeAsync(async db =>
        {
            db.Add(new Memo { Name = "memo" });
            using (db.SuppressAllSaveHandlers())
                await db.SaveChangesAsync(Ct);

            db.Add(new Order { Name = "order", Status = "new" });
            await db.SaveChangesAsync(Ct);
        });

        // The order's handler saves the memo, the memo's handler saves the order, and the order's handler is still
        // handling that order.
        Assert.Equal(["the order's handler", "the memo's handler"], host.Log.Lines);

        await host.InScopeAsync(async db =>
        {
            Assert.Equal("memo +order", await db.Memos.Select(x => x.Name).SingleAsync(Ct));
            Assert.Equal("new +memo", await db.Orders.Select(x => x.Status).SingleAsync(Ct));
        });
    }

    // A service that sends an order somewhere and stores the outcome on it. A handler calls it for the order it was
    // saved with, and a timer calls it for the orders that are still waiting.
    public sealed class Sender(SaveLog log)
    {
        public async Task SendAsync(Db db, Order order, bool switchHandlersOff, CancellationToken cancellationToken)
        {
            log.Add($"sent '{order.Name}'");
            order.Status = "Refused";

            // The switch names the handler that must not start again, and nothing else.
            using (switchHandlersOff ? db.SuppressSaveHandler<SendsNewAndChangedOrders>() : null)
                await db.SaveChangesAsync(cancellationToken);
        }
    }

    public sealed class SendsNewAndChangedOrders(Sender sender) : IAfterCommitHandler<Order>
    {
        public async ValueTask AfterCommitAsync(SavedRows<Order> rows, CancellationToken cancellationToken)
        {
            foreach (var row in rows)
                await sender.SendAsync((Db)rows.Db, row.Entity, switchHandlersOff: false, cancellationToken);
        }
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    public async Task The_same_outcome_save_made_outside_the_handler_starts_the_handler_unless_it_is_switched_off(bool switchHandlersOff, int sentByTheTimer)
    {
        await using var host = SavePipelineHost.Start(services => services
            .AddScoped<Sender>()
            .AddSaveHandler<SendsNewAndChangedOrders>());

        // From the handler: the outcome save writes the record the handler is handling, so it is not called again.
        await host.InScopeAsync(async db =>
        {
            db.Add(new Order { Name = "from the handler" });
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(["sent 'from the handler'"], host.Log.Lines);
        host.Log.Clear();

        // From a timer no handler is running, so nothing keeps the outcome save from starting the handler: the order
        // is sent a second time at once. A service that saves its own outcome keeps a switch around that save.
        await using (var scope = host.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Db>();
            var waiting = new Order { Name = "from the timer" };
            db.Add(waiting);
            using (db.SuppressSaveHandler<SendsNewAndChangedOrders>())
                await db.SaveChangesAsync(Ct);

            await scope.ServiceProvider.GetRequiredService<Sender>().SendAsync(db, waiting, switchHandlersOff, Ct);
        }

        Assert.Equal(sentByTheTimer, host.Log.Lines.Count);
    }

    // ── Sixteen deep at most ────────────────────────────────────────────────────

    public sealed class EachOrderAddsAnother(SaveLog log) : IAfterSaveHandler<Order>
    {
        public async ValueTask AfterSaveAsync(SavedRows<Order> rows, CancellationToken cancellationToken)
        {
            log.Add(rows[0].Entity.Name!);
            rows.Db.Add(new Order { Name = "order " + (int.Parse(rows[0].Entity.Name!.Split(' ')[1]) + 1) });
            await rows.Db.SaveChangesAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task The_17th_save_from_inside_another_throws_names_the_handlers_and_undoes_them_all()
    {
        await using var host = SavePipelineHost.Start(services => services.AddSaveHandler<EachOrderAddsAnother>());

        await host.InScopeAsync(async db =>
        {
            db.Add(new Order { Name = "order 0" });

            var tooDeep = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
            Assert.Contains("17 saves deep", tooDeep.Message);
            Assert.Contains("EachOrderAddsAnother (after save, Order), then EachOrderAddsAnother (after save, Order)", tooDeep.Message);
        });

        // The outer save and sixteen saves inside it ran their handler. The seventeenth was refused.
        Assert.Equal(Enumerable.Range(0, 17).Select(i => "order " + i), host.Log.Lines);
        Assert.Equal(0, await host.InScopeAsync(db => db.Orders.CountAsync(Ct)));
    }
}
