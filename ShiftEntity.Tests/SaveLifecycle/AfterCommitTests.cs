using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Model.Enums;
using ShiftSoftware.ShiftEntity.Tests.Replication;
using ShiftSoftware.ShiftEntity.Tests.SaveHandlers;
using Xunit;
using static ShiftSoftware.ShiftEntity.Tests.SaveHandlers.SavePipelineModel;
using static ShiftSoftware.ShiftEntity.Tests.SaveLifecycle.ScriptedHandlers;

namespace ShiftSoftware.ShiftEntity.Tests.SaveLifecycle;

/// <summary>
/// The stage after the commit: it runs only for committed rows, once for what committed together and in order; a
/// failure in it undoes nothing, stops nothing and reaches whoever committed as one <see cref="AfterCommitException"/>;
/// and a rollback tells nobody. <see cref="ShiftDbContext.AfterCommit(Action)"/> is the same stage, written by hand.
/// </summary>
public class AfterCommitTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── Only for committed rows, once ───────────────────────────────────────────

    // How many orders another connection reads as committed, without waiting for a row that is still locked.
    private static async Task<int> CommittedOrdersAsync(SavePipelineHost host)
    {
        await using var other = host.Bare();

        return host.IsSqlServer
            ? await other.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM [Orders] WITH (READPAST)").SingleAsync(Ct)
            : await other.Orders.CountAsync(Ct);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task After_commit_runs_once_another_connection_can_read_the_rows_and_after_save_runs_before(bool sqlServer, bool appTransaction)
    {
        SavePipelineHost host = null!;
        var seen = new List<string>();

        var script = new Script<Order>
        {
            AfterSave = async (_, _) => seen.Add($"after save: {await CommittedOrdersAsync(host)} committed"),
            AfterCommit = async (rows, _) => seen.Add($"after commit: {await CommittedOrdersAsync(host)} committed, transaction open: {rows.Db.Database.CurrentTransaction is not null}"),
        };

        host = sqlServer
            ? await SavePipelineHost.StartOnSqlServerAsync("AfterCommit", services => services.AddScript(script))
            : SavePipelineHost.StartOnFile(services => services.AddScript(script));

        await using var _ = host;

        await host.InScopeAsync(async db =>
        {
            await using var transaction = appTransaction ? await db.Database.BeginTransactionAsync(Ct) : null;

            db.Add(new Order { Name = "a" });
            await db.SaveChangesAsync(Ct);

            if (transaction is not null)
                await transaction.CommitAsync(Ct);
        });

        Assert.Equal(["after save: 0 committed", "after commit: 1 committed, transaction open: False"], seen);
    }

    [Fact]
    public async Task After_commit_runs_once_for_three_saves_of_one_transaction_and_once_for_each_save_on_its_own()
    {
        await using var host = SavePipelineHost.Start(services => services.AddScript(new Script<Order> { AfterCommit = Nothing }));

        await host.InScopeAsync(async db =>
        {
            await using (var transaction = await db.Database.BeginTransactionAsync(Ct))
            {
                foreach (var name in new[] { "a", "b", "c" })
                {
                    db.Add(new Order { Name = name });
                    await db.SaveChangesAsync(Ct);
                }

                Assert.Empty(host.Log.Lines);
                await transaction.CommitAsync(Ct);
            }

            foreach (var name in new[] { "d", "e" })
            {
                db.Add(new Order { Name = name });
                await db.SaveChangesAsync(Ct);
            }
        });

        Assert.Equal(
        [
            "after commit of Order: Added Order 'a', Added Order 'b', Added Order 'c'",
            "after commit of Order: Added Order 'd'",
            "after commit of Order: Added Order 'e'",
        ], host.Log.Lines);
    }

    // ── A failure after the commit ──────────────────────────────────────────────

    public sealed class FirstFails(SaveLog log) : IAfterCommitHandler<Order>
    {
        public ValueTask AfterCommitAsync(SavedRows<Order> rows, CancellationToken cancellationToken)
        {
            log.Add("first ran");
            throw new InvalidOperationException("The first handler failed.");
        }
    }

    public sealed class SecondWorks(SaveLog log) : IAfterCommitHandler<Order>
    {
        public ValueTask AfterCommitAsync(SavedRows<Order> rows, CancellationToken cancellationToken)
        {
            log.Add("second ran");
            return ValueTask.CompletedTask;
        }
    }

    public sealed class ThirdFails(SaveLog log) : IAfterCommitHandler<Order>
    {
        public async ValueTask AfterCommitAsync(SavedRows<Order> rows, CancellationToken cancellationToken)
        {
            log.Add("third ran");
            await Task.Yield();
            throw new ShiftEntityException(new Message("Failed to send email", "The order was saved, but the email couldn't be sent."));
        }
    }

    private static void ThreeHandlers(IServiceCollection services) =>
        services.AddSaveHandler<FirstFails>().AddSaveHandler<SecondWorks>().AddSaveHandler<ThirdFails>();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task When_two_of_three_handlers_throw_all_three_ran_the_row_stays_and_one_exception_holds_both_failures(bool sync)
    {
        await using var host = SavePipelineHost.Start(ThreeHandlers);

        await host.InScopeAsync(async db =>
        {
            var order = new Order { Name = "saved" };
            db.Add(order);

            // Thrown from SaveChanges: for a save on its own, that is the call that committed.
            var failed = sync
                ? Assert.Throws<AfterCommitException>(() => db.SaveChanges())
                : await Assert.ThrowsAsync<AfterCommitException>(() => db.SaveChangesAsync(Ct));

            Assert.Equal(2, failed.Failures.Count);
            Assert.Equal("The first handler failed.", Assert.IsType<InvalidOperationException>(failed.Failures[0]).Message);
            Assert.Equal("Failed to send email", Assert.IsType<ShiftEntityException>(failed.Failures[1]).Message.Title);
            Assert.Same(failed.Failures[0], failed.InnerException);
            Assert.Contains("2 steps that run after the commit failed", failed.Message);

            // Nothing was undone, and the context is as after any save: it can be used and saved again.
            Assert.Equal(EntityState.Unchanged, db.Entry(order).State);
            order.Status = "Resolved";
            host.Log.Add("saving again");
            await Assert.ThrowsAsync<AfterCommitException>(() => db.SaveChangesAsync(Ct));
        });

        Assert.Equal(
            ["first ran", "second ran", "third ran", "saving again", "first ran", "second ran", "third ran"],
            host.Log.Lines);
        Assert.Equal("Resolved", await host.InScopeAsync(db => db.Orders.Select(x => x.Status).SingleAsync(Ct)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_usual_try_commit_catch_rollback_gives_the_caller_the_after_commit_failure_and_keeps_the_rows(bool sqlServer)
    {
        await using var host = await SavePipelineHost.StartAsync(sqlServer, "CommitRollback", ThreeHandlers);

        await host.InScopeAsync(async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);

            db.Add(new Order { Name = "saved" });
            await db.SaveChangesAsync(Ct);

            // EF's commit has succeeded when the exception leaves Commit. A rollback of the finished transaction would
            // throw "this transaction has completed" and hide it, so after a commit the rollback does nothing.
            var failed = await Assert.ThrowsAsync<AfterCommitException>(async () =>
            {
                try
                {
                    await transaction.CommitAsync(Ct);
                }
                catch
                {
                    await transaction.RollbackAsync(Ct);
                    throw;
                }
            });

            Assert.Equal(2, failed.Failures.Count);
            Assert.Null(db.Database.CurrentTransaction);

            // The synchronous rollback and the facade's do nothing either.
            transaction.Rollback();
        });

        Assert.Equal(["first ran", "second ran", "third ran"], host.Log.Lines);
        Assert.Equal(1, await host.InScopeAsync(db => db.Orders.CountAsync(Ct)));
    }

    /// <summary>How the caller ends the transaction in the usual try-commit-catch-rollback shape.</summary>
    public enum Ending
    {
        /// <summary><c>Database.CommitTransaction()</c> and <c>Database.RollbackTransaction()</c>.</summary>
        Facade,

        /// <summary><c>Database.CommitTransactionAsync()</c> and <c>Database.RollbackTransactionAsync()</c>.</summary>
        FacadeAsync,

        /// <summary>The transaction object's <c>CommitAsync</c> and <c>RollbackAsync</c>.</summary>
        TransactionObject,
    }

    [Theory]
    [InlineData(false, Ending.Facade)]
    [InlineData(false, Ending.FacadeAsync)]
    [InlineData(false, Ending.TransactionObject)]
    [InlineData(true, Ending.Facade)]
    [InlineData(true, Ending.FacadeAsync)]
    [InlineData(true, Ending.TransactionObject)]
    public async Task The_usual_try_commit_catch_rollback_through_Database_gives_the_caller_the_after_commit_failure_too(bool sqlServer, Ending ending)
    {
        // The same shape as above, with the facade's own methods. After a commit that succeeded, Database's rollback
        // does nothing, as the transaction object's does: EF holds no transaction any more, and its "no active
        // transaction" would replace the failure the caller has to see.
        await using var host = await SavePipelineHost.StartAsync(sqlServer, "FacadeCommitRollback", ThreeHandlers);
        Exception? seen = null;

        await host.InScopeAsync(async db =>
        {
            try
            {
                var transaction = ending == Ending.Facade ? db.Database.BeginTransaction() : await db.Database.BeginTransactionAsync(Ct);

                try
                {
                    db.Add(new Order { Name = "saved" });

                    switch (ending)
                    {
                        case Ending.Facade:
                            db.SaveChanges();
                            db.Database.CommitTransaction();
                            break;
                        case Ending.FacadeAsync:
                            await db.SaveChangesAsync(Ct);
                            await db.Database.CommitTransactionAsync(Ct);
                            break;
                        default:
                            await db.SaveChangesAsync(Ct);
                            await transaction.CommitAsync(Ct);
                            break;
                    }
                }
                catch
                {
                    switch (ending)
                    {
                        case Ending.Facade:
                            db.Database.RollbackTransaction();
                            break;
                        case Ending.FacadeAsync:
                            await db.Database.RollbackTransactionAsync(Ct);
                            break;
                        default:
                            await transaction.RollbackAsync(Ct);
                            break;
                    }

                    throw;
                }
            }
            catch (Exception exception)
            {
                seen = exception;
            }
        });

        Assert.Equal(2, Assert.IsType<AfterCommitException>(seen).Failures.Count);
        Assert.Equal(["first ran", "second ran", "third ran"], host.Log.Lines);
        Assert.Equal(1, await host.InScopeAsync(db => db.Orders.CountAsync(Ct)));
    }

    [Fact]
    public async Task Databases_rollback_throws_as_EFs_does_where_there_never_was_a_transaction_and_rolls_back_the_next_one_it_begins()
    {
        await using var host = SavePipelineHost.Start(services => services.AddScript(new Script<Order> { AfterCommit = Nothing }));

        await host.InScopeAsync(async db =>
        {
            // Never a transaction: EF's own answer.
            Assert.Throws<InvalidOperationException>(() => db.Database.RollbackTransaction());
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.Database.RollbackTransactionAsync(Ct));

            // After a commit that succeeded: nothing, through either method.
            await db.Database.BeginTransactionAsync(Ct);
            db.Add(new Order { Name = "committed" });
            await db.SaveChangesAsync(Ct);
            await db.Database.CommitTransactionAsync(Ct);

            db.Database.RollbackTransaction();
            await db.Database.RollbackTransactionAsync(Ct);

            // The next transaction it begins is the one a rollback is for.
            await db.Database.BeginTransactionAsync(Ct);
            db.Add(new Order { Name = "rolled back" });
            await db.SaveChangesAsync(Ct);
            await db.Database.RollbackTransactionAsync(Ct);
            Assert.Null(db.Database.CurrentTransaction);

            // After a rollback, a second one throws as EF's does.
            Assert.Throws<InvalidOperationException>(() => db.Database.RollbackTransaction());
        });

        Assert.Equal(["committed"], await host.InScopeAsync(db => db.Orders.Select(x => x.Name).ToListAsync(Ct)));
        Assert.Equal(["after commit of Order: Added Order 'committed'"], host.Log.Lines);
    }

    // The usual try-commit-catch-rollback shape through the facade, as the caller writes it. Returns what the caller's
    // catch passed on.
    private static async Task<Exception?> CommitOrRollBackThroughDatabaseAsync(SavePipelineHost host, bool async)
    {
        Exception? seen = null;

        await host.InScopeAsync(async db =>
        {
            try
            {
                if (async)
                    await db.Database.BeginTransactionAsync(Ct);
                else
                    db.Database.BeginTransaction();

                try
                {
                    db.Add(new Memo { Name = "committed" });

                    if (async)
                    {
                        await db.SaveChangesAsync(Ct);
                        await db.Database.CommitTransactionAsync(Ct);
                    }
                    else
                    {
                        db.SaveChanges();
                        db.Database.CommitTransaction();
                    }
                }
                catch
                {
                    if (async)
                        await db.Database.RollbackTransactionAsync(Ct);
                    else
                        db.Database.RollbackTransaction();

                    throw;
                }
            }
            catch (Exception exception)
            {
                seen = exception;
            }
        });

        return seen;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Databases_rollback_after_a_commit_whose_after_commit_handler_made_a_save_that_failed_gives_the_caller_the_after_commit_failure(bool sqlServer, bool async)
    {
        // The memo's after-commit handler saves a ticket on the same context. A ticket has an after-save handler, so that
        // save opens a transaction of its own, and when the ticket's handler fails, it rolls that transaction back. The
        // caller's rollback is for the caller's transaction, which committed: it does nothing, and the caller gets the
        // failure, not EF's "no active transaction".
        await using var host = await SavePipelineHost.StartAsync(sqlServer, "FacadeAfterAFailedSave", services => services
            .AddScript(new Script<Memo>
            {
                AfterCommit = async (rows, ct) =>
                {
                    rows.Db.Add(new Ticket { Queue = "follow-up", Name = "follow-up" });
                    await rows.Db.SaveChangesAsync(ct);
                },
            })
            .AddScript(new Script<Ticket>
            {
                AfterSave = (_, _) => throw new InvalidOperationException("The follow-up could not be saved."),
            }));

        var seen = await CommitOrRollBackThroughDatabaseAsync(host, async);

        Assert.Equal("The follow-up could not be saved.", Assert.Single(Assert.IsType<AfterCommitException>(seen).Failures).Message);
        Assert.Equal(["committed"], await host.InScopeAsync(db => db.Memos.Select(x => x.Name).ToListAsync(Ct)));
        Assert.Equal(0, await host.InScopeAsync(db => db.Tickets.CountAsync(Ct)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Databases_rollback_after_a_commit_whose_after_commit_handler_began_and_rolled_back_a_transaction_gives_the_caller_the_failure(bool sqlServer, bool async)
    {
        // A transaction the handler begins and rolls back through the facade ends after the caller's committed. The
        // caller's rollback is still for the caller's own transaction.
        await using var host = await SavePipelineHost.StartAsync(sqlServer, "FacadeAfterAHandlersTransaction", services => services.AddScript(new Script<Memo>
        {
            AfterCommit = async (rows, ct) =>
            {
                await rows.Db.Database.BeginTransactionAsync(ct);
                rows.Db.Add(new Ticket { Queue = "follow-up", Name = "rolled back" });
                await rows.Db.SaveChangesAsync(ct);
                await rows.Db.Database.RollbackTransactionAsync(ct);

                throw new InvalidOperationException("The email could not be sent.");
            },
        }));

        var seen = await CommitOrRollBackThroughDatabaseAsync(host, async);

        Assert.Equal("The email could not be sent.", Assert.Single(Assert.IsType<AfterCommitException>(seen).Failures).Message);
        Assert.Equal(["committed"], await host.InScopeAsync(db => db.Memos.Select(x => x.Name).ToListAsync(Ct)));
        Assert.Equal(0, await host.InScopeAsync(db => db.Tickets.CountAsync(Ct)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Without_a_transaction_of_the_apps_a_rollback_through_Database_after_a_save_throws_as_EFs_does_whatever_the_saved_type_has(bool sqlServer)
    {
        // The transaction a save opens for itself is not the app's: a rollback through Database after such a save is a
        // rollback with no transaction, and EF says so, as it does after a save that needed none.
        await using var host = await SavePipelineHost.StartAsync(sqlServer, "StrayRollback", services => services.AddScript(new Script<Memo> { AfterSave = Nothing }));

        static void AssertEFsAnswer(InvalidOperationException refused) => Assert.Contains("does not have any active transactions", refused.Message);

        await host.InScopeAsync(async db =>
        {
            // A memo has an after-save handler, so its save opens and commits a transaction of its own.
            db.Add(new Memo { Name = "in a transaction of the save's own" });
            await db.SaveChangesAsync(Ct);
            Assert.Equal(["BEGIN", "INSERT Memos", "COMMIT"], host.Sql.Writes);

            AssertEFsAnswer(Assert.Throws<InvalidOperationException>(() => db.Database.RollbackTransaction()));
            AssertEFsAnswer(await Assert.ThrowsAsync<InvalidOperationException>(() => db.Database.RollbackTransactionAsync(Ct)));

            // A ticket has no handler, and its save opens none.
            db.Add(new Ticket { Queue = "q", Name = "without a transaction" });
            await db.SaveChangesAsync(Ct);

            AssertEFsAnswer(Assert.Throws<InvalidOperationException>(() => db.Database.RollbackTransaction()));
            AssertEFsAnswer(await Assert.ThrowsAsync<InvalidOperationException>(() => db.Database.RollbackTransactionAsync(Ct)));
        });

        Assert.Equal(1, await host.InScopeAsync(db => db.Memos.CountAsync(Ct)));
        Assert.Equal(1, await host.InScopeAsync(db => db.Tickets.CountAsync(Ct)));
    }

    public sealed class FailingPreparer : IShiftEntityPrepareForReplicationAsync<PinModel.Item>
    {
        public ValueTask<PinModel.Item> PrepareForReplicationAsync(PinModel.Item entity, ReplicationChangeType changeType) =>
            entity.Code == "fails" ? throw new InvalidOperationException("The preparer could not read what it needs.") : ValueTask.FromResult(entity);
    }

    public sealed class ItemCommitted(SaveLog log) : IAfterCommitHandler<PinModel.Item>
    {
        public ValueTask AfterCommitAsync(SavedRows<PinModel.Item> rows, CancellationToken cancellationToken)
        {
            log.Add($"after commit of {rows.Count} item(s)");
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task A_replication_preparer_that_throws_after_the_write_of_a_save_with_no_transaction_is_an_after_commit_failure()
    {
        // The row is committed when the preparer runs, so its failure can undo nothing. The rest still runs, and the
        // caller is told. Inside a transaction the same failure fails the save, which is undone.
        var log = new SaveLog();

        await using var host = new ReplicationPinHost(
            (o, client) => o.SetUpReplication<PinModel.Db, PinModel.Item>(client, ReplicationPinHost.Database)
                .Replicate<PinModel.ItemDocument>("items", x => x.id, x => PinModel.Document(x.Entity)),
            services => services
                .AddSingleton(log)
                .AddScoped<IShiftEntityPrepareForReplicationAsync<PinModel.Item>, FailingPreparer>()
                .AddSaveHandler<ItemCommitted>(),
            containers: ("items", ["/id"]));

        await using (var scope = host.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PinModel.Db>();
            db.Items.Add(new PinModel.Item { Code = "fails", Region = "north" });

            var failed = await Assert.ThrowsAsync<AfterCommitException>(() => db.SaveChangesAsync(Ct));
            Assert.Equal("The preparer could not read what it needs.", Assert.Single(failed.Failures).Message);

            // The context is usable afterwards, and the row is saved.
            db.Items.Add(new PinModel.Item { Code = "works", Region = "north" });
            await db.SaveChangesAsync(Ct);

            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            db.Items.Add(new PinModel.Item { Code = "fails", Region = "south" });
            var inTransaction = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
            Assert.Equal("The preparer could not read what it needs.", inTransaction.Message);
            await transaction.CommitAsync(Ct);
        }

        await host.Log.AllFinishedAsync();

        Assert.Equal(["after commit of 1 item(s)", "after commit of 1 item(s)"], log.Lines);
        Assert.True(await host.ExistsAsync<PinModel.Item>(1));
        Assert.True(await host.ExistsAsync<PinModel.Item>(2));
        Assert.False(await host.ExistsAsync<PinModel.Item>(3));
        Assert.Equal(["Item 2"], host.Log.Started);
    }

    // ── A rollback tells nobody ─────────────────────────────────────────────────

    // Rolls every save back the way a dry run does: it saves for real, reads what the save produced, and rolls back.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_dry_run_that_saves_and_rolls_back_lets_nothing_leave(bool sqlServer)
    {
        await using var host = await SavePipelineHost.StartAsync(sqlServer, "DryRun", services => services
            .AddScript(new Script<Order> { AfterSave = Nothing, BeforeCommit = Nothing, AfterCommit = Nothing })
            .AddScoped<IShiftDbContextSaveObserver, QueuesWorkForEachSave>());

        await host.InScopeAsync(async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);

            var order = new Order { Name = "tried" };
            db.Add(order);
            await db.SaveChangesAsync(Ct);
            db.AfterCommit(() => host.Log.Add("work queued in code ran"));

            Assert.True(order.ID > 0);
            await transaction.RollbackAsync(Ct);
        });

        Assert.Equal(["after save of Order: Added Order 'tried'"], host.Log.Lines);
        Assert.Equal(0, await host.InScopeAsync(db => db.Orders.CountAsync(Ct)));
    }

    // Queues one piece of work for each save it is told about, as replication and the notification rules do.
    internal sealed class QueuesWorkForEachSave(SaveLog log) : IShiftDbContextSaveObserver
    {
        public ValueTask SavedAsync(ShiftDbContext context, SaveReport report, CancellationToken cancellationToken)
        {
            var rows = string.Join(", ", report.Rows.Select(row => row.Entity).OfType<INamed>().Select(row => row.Name));
            context.AfterCommit(() => log.Add($"queued by the observer for: {rows}"));
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task A_save_that_fails_takes_what_it_queued_with_it_and_the_saves_before_it_keep_theirs()
    {
        await using var host = SavePipelineHost.Start(services => services
            .AddScript(new Script<Order>
            {
                AfterSave = (rows, _) =>
                {
                    rows.Db.AfterCommit(() => host_Log(rows.Services).Add($"queued by the handler for: {rows[0].Entity.Name}"));
                    return rows[0].Entity.Name == "fails" ? throw new InvalidOperationException("The handler failed.") : Task.CompletedTask;
                },
                AfterCommit = Nothing,
            })
            .AddScoped<IShiftDbContextSaveObserver, QueuesWorkForEachSave>());

        await host.InScopeAsync(async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);

            db.Add(new Order { Name = "kept" });
            await db.SaveChangesAsync(Ct);

            db.Add(new Order { Name = "fails" });
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
            db.ChangeTracker.Clear();

            host.Log.Add("commit");
            await transaction.CommitAsync(Ct);
        });

        Assert.Equal(
        [
            "after save of Order: Added Order 'kept'",
            "after save of Order: Added Order 'fails'",
            "commit",
            "queued by the observer for: kept",
            "queued by the handler for: kept",
            "after commit of Order: Added Order 'kept'",
        ], host.Log.Lines);
    }

    private static SaveLog host_Log(IServiceProvider services) => services.GetRequiredService<SaveLog>();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_transaction_committed_through_its_ADO_NET_object_runs_nothing_and_an_error_says_what_was_dropped(bool sqlServer)
    {
        await using var host = await SavePipelineHost.StartAsync(sqlServer, "AdoCommit", services => services.AddScript(new Script<Order>
        {
            AfterCommit = Nothing,
        }));

        await host.InScopeAsync(async db =>
        {
            await using (var transaction = await db.Database.BeginTransactionAsync(Ct))
            {
                db.Add(new Order { Name = "committed around EF" });
                await db.SaveChangesAsync(Ct);
                db.AfterCommit(() => host.Log.Add("queued work ran"));

                // EF is not told, so neither is the context. It finds out when the transaction is disposed.
                await transaction.GetDbTransaction().CommitAsync(Ct);
            }

            Assert.Null(db.Database.CurrentTransaction);
        });

        Assert.Empty(host.Log.Lines);
        Assert.Equal(1, await host.InScopeAsync(db => db.Orders.CountAsync(Ct)));

        var error = Assert.Single(host.Logs.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Contains("ended without a commit this context could follow", error.Message);
        Assert.Contains("1 piece(s) of work, and the after-commit handlers of 1 row(s)", error.Message);
    }

    [Fact]
    public async Task A_context_disposed_with_a_transaction_open_drops_what_waited_with_a_warning()
    {
        await using var host = SavePipelineHost.Start(services => services.AddScript(new Script<Order> { AfterCommit = Nothing }));

        await host.InScopeAsync(async db =>
        {
            // Never committed, never rolled back and never disposed: the context goes away with it.
            await db.Database.BeginTransactionAsync(Ct);
            db.Add(new Order { Name = "left open" });
            await db.SaveChangesAsync(Ct);
        });

        Assert.Empty(host.Log.Lines);
        Assert.Equal(0, await host.InScopeAsync(db => db.Orders.CountAsync(Ct)));

        var warning = Assert.Single(host.Logs.Entries, entry => entry.Message.Contains("ended without a commit this context could follow"));
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("the context was disposed", warning.Message);
    }

    // Fails the commit itself, as a connection that is lost while the server commits does.
    public sealed class FailingCommit : DbTransactionInterceptor
    {
        public bool Armed { get; set; }

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(System.Data.Common.DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) =>
            Armed ? throw new TimeoutException("The commit timed out (simulated).") : ValueTask.FromResult(result);
    }

    [Fact]
    public async Task When_the_commit_itself_throws_the_outcome_is_unknown_so_nothing_runs_and_an_error_says_what_was_dropped()
    {
        var failing = new FailingCommit();

        await using var host = SavePipelineHost.Start(
            services => services.AddScript(new Script<Order> { AfterCommit = Nothing }),
            context => context.AddInterceptors(failing));

        await host.InScopeAsync(async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            db.Add(new Order { Name = "a" });
            await db.SaveChangesAsync(Ct);

            failing.Armed = true;
            await Assert.ThrowsAsync<TimeoutException>(() => transaction.CommitAsync(Ct));
            failing.Armed = false;
        });

        Assert.Empty(host.Log.Lines);

        var error = Assert.Single(host.Logs.Entries, entry => entry.Level == LogLevel.Error && entry.Message.Contains("its commit failed"));
        Assert.Contains("the after-commit handlers of 1 row(s)", error.Message);
    }

    // ── AfterCommit, the stage written by hand ──────────────────────────────────

    private static Task SendAsync(SaveLog log, string what, bool fail = false) => Task.Run(async () =>
    {
        await Task.Delay(20);
        log.Add($"sent {what}");

        if (fail)
            throw new InvalidOperationException($"Could not send {what}.");
    });

    [Fact]
    public async Task Work_that_returns_a_task_is_waited_for_and_its_failure_reaches_the_caller()
    {
        await using var host = SavePipelineHost.Start();

        await host.InScopeAsync(async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            db.Add(new Memo { Name = "a" });
            await db.SaveChangesAsync(Ct);

            // Both bind to the overload that takes a task, not to the one that takes an Action: the task is awaited,
            // and its failure is not lost.
            db.AfterCommit(() => SendAsync(host.Log, "the first"));
            db.AfterCommit(async () => await SendAsync(host.Log, "the second", fail: true));
            db.AfterCommit(async token =>
            {
                await SendAsync(host.Log, "the third");
                host.Log.Add($"the token can be cancelled: {token.CanBeCanceled}");
            });
            db.AfterCommit(() => host.Log.Add("the fourth, a plain action"));

            Assert.Empty(host.Log.Lines);

            var failed = await Assert.ThrowsAsync<AfterCommitException>(() => transaction.CommitAsync(Ct));
            Assert.Equal("Could not send the second.", Assert.Single(failed.Failures).Message);
        });

        // In the order it was queued, each finished before the next began.
        Assert.Equal(
            ["sent the first", "sent the second", "sent the third", "the token can be cancelled: False", "the fourth, a plain action"],
            host.Log.Lines);
    }

    [Fact]
    public async Task With_no_save_and_no_transaction_in_progress_the_work_runs_at_once_and_is_waited_for()
    {
        await using var host = SavePipelineHost.Start();

        await host.InScopeAsync(db =>
        {
            db.AfterCommit(() => SendAsync(host.Log, "at once"));
            host.Log.Add("the call returned");

            var failure = Assert.Throws<InvalidOperationException>(() => db.AfterCommit(() => SendAsync(host.Log, "and failed", fail: true)));
            Assert.Equal("Could not send and failed.", failure.Message);
            return Task.CompletedTask;
        });

        Assert.Equal(["sent at once", "the call returned", "sent and failed"], host.Log.Lines);
    }

    [Fact]
    public async Task Work_queued_inside_a_save_with_no_transaction_runs_at_the_end_of_that_save_before_the_handlers()
    {
        await using var host = SavePipelineHost.Start(services => services
            .AddScript(new Script<Order>
            {
                AfterCommit = (rows, _) =>
                {
                    // Queued after the commit: it runs before the next handler.
                    rows.Db.AfterCommit(() => host_Log(rows.Services).Add("queued by the first handler"));
                    return Task.CompletedTask;
                },
            })
            .AddSaveHandler<CommitLog<INamed>>()
            .AddScoped<IShiftDbContextSaveObserver, QueuesWorkForEachSave>());

        await host.InScopeAsync(async db =>
        {
            db.Add(new Order { Name = "a" });
            await db.SaveChangesAsync(Ct);
            host.Log.Add("the save returned");
        });

        Assert.Equal(
        [
            "queued by the observer for: a",
            "after commit of Order: Added Order 'a'",
            "queued by the first handler",
            "after commit of INamed: Added Order 'a'",
            "the save returned",
        ], host.Log.Lines);
    }

    // Stands in for a host that is stopping.
    public sealed class Lifetime : IHostApplicationLifetime
    {
        public readonly CancellationTokenSource Stopping = new();

        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => Stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => Stopping.Cancel();
    }

    [Fact]
    public async Task The_token_after_the_commit_is_the_hosts_and_is_cancelled_only_when_the_host_stops()
    {
        var lifetime = new Lifetime();
        var tokens = new List<bool>();

        await using var host = SavePipelineHost.Start(services => services
            .AddSingleton<IHostApplicationLifetime>(lifetime)
            .AddScript(new Script<Order>
            {
                AfterCommit = (_, ct) =>
                {
                    tokens.Add(ct == lifetime.Stopping.Token);
                    return Task.CompletedTask;
                },
            }));

        using var caller = new CancellationTokenSource();

        await host.InScopeAsync(async db =>
        {
            db.Add(new Order { Name = "a" });
            await db.SaveChangesAsync(caller.Token);

            db.AfterCommit(token =>
            {
                tokens.Add(token == lifetime.Stopping.Token);
                return Task.CompletedTask;
            });
        });

        Assert.Equal([true, true], tokens);
    }
}
