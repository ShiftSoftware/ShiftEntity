using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.Tests.SaveHandlers;
using Xunit;
using static ShiftSoftware.ShiftEntity.Tests.SaveHandlers.SavePipelineModel;
using static ShiftSoftware.ShiftEntity.Tests.SaveLifecycle.ScriptedHandlers;

namespace ShiftSoftware.ShiftEntity.Tests.SaveLifecycle;

/// <summary>
/// A save that fails before its commit leaves the database as it was before that save. These pin how: the context's
/// own transaction is rolled back, a save inside the app's transaction is rolled back to a savepoint, and where no
/// savepoint is possible, or the server has already ended the transaction, the transaction can only be rolled back.
/// EF has accepted the rows of such a save, so the context refuses to save again until it is cleared.
/// </summary>
public class SaveFailureTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Script<Order> FailsAfterSaveOn(string name) => new()
    {
        AfterSave = (rows, _) => rows.Any(row => row.Entity.Name == name) ? throw new InvalidOperationException("The handler failed.") : Task.CompletedTask,
        AfterCommit = Nothing,
    };

    // ── The context's own transaction ───────────────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_after_save_handler_that_throws_fails_the_save_and_leaves_the_table_as_it_was(bool sqlServer)
    {
        await using var host = await SavePipelineHost.StartAsync(sqlServer, "AfterSaveFails", services => services.AddScript(FailsAfterSaveOn("fails")));

        await host.InScopeAsync(async db =>
        {
            var order = new Order { Name = "fails" };
            db.Add(order);

            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
            Assert.Equal("The handler failed.", failure.Message);
            Assert.Equal(["BEGIN", "INSERT Orders", "ROLLBACK"], host.Sql.Writes);
            Assert.Null(db.Database.CurrentTransaction);

            // The database no longer has the row, but EF accepted it: the context shows it as saved, with its key.
            Assert.Equal(EntityState.Unchanged, db.Entry(order).State);
            Assert.True(order.ID > 0);

            // Saving again would write nothing for it and report no error, so the context refuses.
            var stale = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
            Assert.Contains("holds rows of a save that was rolled back, a row of Order among them", stale.Message);
            Assert.Throws<InvalidOperationException>(() => db.SaveChanges());

            // Cleared, the context saves again, and the same record can be given to it anew.
            db.ChangeTracker.Clear();
            order.ID = 0;
            order.Name = "tried again";
            db.Add(order);
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(["tried again"], await host.InScopeAsync(db => db.Orders.Select(x => x.Name).ToListAsync(Ct)));
        Assert.Equal(
        [
            "after save of Order: Added Order 'fails'",
            "after save of Order: Added Order 'tried again'",
            "after commit of Order: Added Order 'tried again'",
        ], host.Log.Lines);
    }

    [Fact]
    public async Task A_row_that_is_given_to_the_context_anew_ends_the_refusal_without_clearing_everything()
    {
        await using var host = SavePipelineHost.Start(services => services.AddScript(FailsAfterSaveOn("fails")));

        await host.InScopeAsync(async db =>
        {
            var order = new Order { Name = "fails" };
            db.Add(order);
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));

            // The app stops tracking the row the context remembers, and adds it again.
            db.Entry(order).State = EntityState.Detached;
            order.ID = 0;
            order.Name = "fixed";
            db.Add(order);

            Assert.Equal(1, await db.SaveChangesAsync(Ct));
        });

        Assert.Equal(["fixed"], await host.InScopeAsync(db => db.Orders.Select(x => x.Name).ToListAsync(Ct)));
    }

    [Fact]
    public async Task A_before_commit_handler_that_throws_fails_a_save_with_its_own_transaction_and_undoes_it()
    {
        await using var host = SavePipelineHost.Start(services => services.AddScript(new Script<Order>
        {
            BeforeCommit = (_, _) => throw new InvalidOperationException("Not this total."),
            AfterCommit = Nothing,
        }));

        await host.InScopeAsync(async db =>
        {
            db.Add(new Order { Name = "a" });

            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
            Assert.Equal("Not this total.", failure.Message);
            Assert.Equal(["BEGIN", "INSERT Orders", "ROLLBACK"], host.Sql.Writes);
        });

        Assert.Equal(["before commit of Order: Added Order 'a'"], host.Log.Lines);
        Assert.Equal(0, await host.InScopeAsync(db => db.Orders.CountAsync(Ct)));
    }

    // ── The app's transaction ───────────────────────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_before_commit_handler_that_throws_stops_the_commit_and_the_apps_transaction_can_only_be_rolled_back(bool sqlServer)
    {
        await using var host = await SavePipelineHost.StartAsync(sqlServer, "BeforeCommitFails", services => services.AddScript(new Script<Order>
        {
            BeforeCommit = (_, _) => throw new InvalidOperationException("Not this total."),
            AfterCommit = Nothing,
        }));

        await host.InScopeAsync(async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            db.Add(new Order { Name = "a" });
            await db.SaveChangesAsync(Ct);

            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.CommitAsync(Ct));
            Assert.Equal("Not this total.", failure.Message);

            // Still open, and the app decides what to do with it. A second commit is refused.
            Assert.Same(transaction, db.Database.CurrentTransaction);
            var again = await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.CommitAsync(Ct));
            Assert.Contains("can only be rolled back: a before-commit handler failed", again.Message);
        });

        Assert.Equal(["before commit of Order: Added Order 'a'"], host.Log.Lines);
        Assert.Equal(0, await host.InScopeAsync(db => db.Orders.CountAsync(Ct)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_write_that_fails_behaves_as_in_EF_inside_and_outside_a_transaction(bool sqlServer)
    {
        // A line of an order that does not exist: the database refuses the row. An after-save handler has lines, so
        // the save runs with the context's transaction, or with the framework's savepoint.
        await using var host = await SavePipelineHost.StartAsync(sqlServer, "WriteFails", services => services.AddScript(new Script<OrderLine>
        {
            AfterSave = Nothing,
            AfterCommit = Nothing,
        }));

        await host.InScopeAsync(async db =>
        {
            var order = new Order { Name = "a" };
            db.Add(order);
            await db.SaveChangesAsync(Ct);

            var line = new OrderLine { OrderID = order.ID + 1000, Note = "of no order" };
            db.Add(line);
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));

            // Nothing was accepted, so the context is not refused: the caller repairs the row and saves again.
            Assert.Equal(EntityState.Added, db.Entry(line).State);
            line.OrderID = order.ID;
            Assert.Equal(1, await db.SaveChangesAsync(Ct));

            await using var transaction = await db.Database.BeginTransactionAsync(Ct);

            db.Add(new OrderLine { OrderID = order.ID, Note = "kept" });
            await db.SaveChangesAsync(Ct);

            var second = new OrderLine { OrderID = order.ID + 1000, Note = "of no order either" };
            db.Add(second);
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));

            // The failed write was undone to the savepoint, and the transaction goes on.
            db.Entry(second).State = EntityState.Detached;
            await transaction.CommitAsync(Ct);
        });

        Assert.Equal(["of no order", "kept"], await host.InScopeAsync(db => db.Lines.OrderBy(x => x.ID).Select(x => x.Note).ToListAsync(Ct)));
        Assert.Equal(
        [
            "after save of OrderLine: Added OrderLine ''", "after commit of OrderLine: Added OrderLine ''",
            "after save of OrderLine: Added OrderLine ''", "after commit of OrderLine: Added OrderLine ''",
        ], host.Log.Lines);
    }

    [Fact]
    public async Task A_cancellation_before_the_commit_is_a_failure_and_is_undone_and_after_the_commit_the_token_is_not_the_callers()
    {
        using var caller = new CancellationTokenSource();
        var tokens = new List<string>();

        await using var host = SavePipelineHost.Start(services => services.AddScript(new Script<Order>
        {
            AfterSave = (rows, ct) =>
            {
                tokens.Add($"after save: the caller's token {ct == caller.Token}");

                if (rows[0].Entity.Name == "cancelled")
                {
                    caller.Cancel();
                    ct.ThrowIfCancellationRequested();
                }

                return Task.CompletedTask;
            },
            BeforeCommit = (_, ct) =>
            {
                tokens.Add($"before commit: the caller's token {ct == caller.Token}");
                return Task.CompletedTask;
            },
            AfterCommit = (_, ct) =>
            {
                // A request that is aborted after the commit must not lose what follows the commit.
                tokens.Add($"after commit: the caller's token {ct == caller.Token}, can be cancelled {ct.CanBeCanceled}");
                return Task.CompletedTask;
            },
        }));

        await host.InScopeAsync(async db =>
        {
            db.Add(new Order { Name = "saved" });
            await db.SaveChangesAsync(caller.Token);
        });

        await host.InScopeAsync(async db =>
        {
            db.Add(new Order { Name = "cancelled" });
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.SaveChangesAsync(caller.Token));
        });

        Assert.Equal(
        [
            "after save: the caller's token True",
            "before commit: the caller's token True",
            "after commit: the caller's token False, can be cancelled False",
            "after save: the caller's token True",
        ], tokens);
        Assert.Equal(["saved"], await host.InScopeAsync(db => db.Orders.Select(x => x.Name).ToListAsync(Ct)));
    }

    // ── SQL Server: where no savepoint is possible, or the server ends the transaction ──

    [Fact]
    public async Task With_MultipleActiveResultSets_there_is_no_savepoint_so_a_save_that_fails_after_its_write_makes_the_transaction_rollback_only()
    {
        await using var host = await SavePipelineHost.StartOnSqlServerAsync("Mars",
            services => services.AddScript(FailsAfterSaveOn("fails")),
            connectionString: connection => connection + ";MultipleActiveResultSets=True");

        await host.InScopeAsync(async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            Assert.False(transaction.SupportsSavepoints);

            db.Add(new Order { Name = "first" });
            await db.SaveChangesAsync(Ct);

            db.Add(new Order { Name = "fails" });
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
            Assert.Equal("The handler failed.", failure.Message);
            Assert.DoesNotContain("SAVEPOINT", host.Sql.Writes);

            // The failed save's row is still in the transaction, so the transaction must not commit.
            var commit = await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.CommitAsync(Ct));
            Assert.Contains("can only be rolled back", commit.Message);
            Assert.Contains("without a savepoint", commit.Message);
        });

        Assert.DoesNotContain(host.Log.Lines, line => line.StartsWith("after commit"));
        Assert.Equal(0, await host.InScopeAsync(db => db.Orders.CountAsync(Ct)));
    }

    [Fact]
    public async Task When_an_aborted_batch_has_ended_the_transaction_the_caller_sees_the_first_error_and_the_transaction_is_rollback_only()
    {
        // With XACT_ABORT on, a run-time error ends the whole transaction on the server. The rollback to the
        // framework's savepoint then has nothing to roll back in.
        await using var host = await SavePipelineHost.StartOnSqlServerAsync("AbortedBatch", services => services.AddScript(new Script<Order>
        {
            AfterSave = async (rows, ct) =>
            {
                if (rows.Any(row => row.Entity.Name == "fails"))
                    await rows.Db.Database.ExecuteSqlRawAsync("SET XACT_ABORT ON; SELECT CAST('not a number' AS int);", ct);
            },
            AfterCommit = Nothing,
        }));

        await host.InScopeAsync(async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);

            db.Add(new Order { Name = "first" });
            await db.SaveChangesAsync(Ct);

            db.Add(new Order { Name = "fails" });
            var first = await Assert.ThrowsAsync<SqlException>(() => db.SaveChangesAsync(Ct));
            Assert.Equal(245, first.Number);      // the conversion failed, and nothing hides it

            // The server has ended the transaction, and ADO.NET knows.
            Assert.Null(transaction.GetDbTransaction().Connection);

            var commit = await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.CommitAsync(Ct));
            Assert.Contains("can only be rolled back", commit.Message);

            // Both saves are gone, and the context holds their rows.
            Assert.Contains("holds rows of a save that was rolled back",
                (await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct))).Message);
        });

        Assert.DoesNotContain(host.Log.Lines, line => line.StartsWith("after commit"));
        Assert.Equal(0, await host.InScopeAsync(db => db.Orders.CountAsync(Ct)));
    }

    [Fact]
    public async Task When_the_server_ends_the_transaction_during_the_write_itself_the_transaction_is_rollback_only_too()
    {
        // Nothing acts after this write, so the save has EF's own savepoint and none of the framework's whose rollback
        // could fail and tell. The context finds out from the transaction itself.
        await using var host = await SavePipelineHost.StartOnSqlServerAsync("AbortedWrite", services => services.AddScript(new Script<Order>
        {
            AfterCommit = Nothing,
        }));

        await host.InScopeAsync(async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            await db.Database.ExecuteSqlRawAsync("SET XACT_ABORT ON;", Ct);

            db.Add(new Order { Name = "first" });
            await db.SaveChangesAsync(Ct);

            // A line of an order that does not exist. The foreign key fails, and with XACT_ABORT on that ends the
            // whole transaction.
            db.Add(new OrderLine { OrderID = 123456789, Note = "of no order" });
            var first = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
            Assert.Equal(547, Assert.IsType<SqlException>(first.InnerException).Number);

            Assert.Null(transaction.GetDbTransaction().Connection);

            var commit = await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.CommitAsync(Ct));
            Assert.Contains("can only be rolled back", commit.Message);
            Assert.Contains("the database ended it", commit.Message);
        });

        // The outcome is known, a rollback, so the dispose reports nothing as lost without a trace.
        Assert.DoesNotContain(host.Logs.Entries, entry => entry.Message.Contains("ended without a commit this context could follow"));
        Assert.Empty(host.Log.Lines);
        Assert.Equal(0, await host.InScopeAsync(db => db.Orders.CountAsync(Ct)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_save_in_a_transaction_that_has_ended_already_is_refused_where_no_savepoint_would_tell(bool mars)
    {
        // ADO.NET runs a command outside any transaction once its transaction has ended, so the save would be committed
        // on its own. With MARS, or with savepoints switched off, nothing that runs before the write notices.
        await using var host = await SavePipelineHost.StartOnSqlServerAsync("EndedBeforeASave",
            connectionString: connection => mars ? connection + ";MultipleActiveResultSets=True" : connection);

        await host.InScopeAsync(async db =>
        {
            db.Database.AutoSavepointsEnabled = mars;

            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            db.Add(new Order { Name = "first" });
            await db.SaveChangesAsync(Ct);

            // SQL of the app's own that ends the transaction, and whose error the app does not pass on.
            await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync("SET XACT_ABORT ON; SELECT CAST('not a number' AS int);", Ct));

            db.Add(new Order { Name = "on its own" });
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
            Assert.Contains("the transaction it is in has ended already", refused.Message);
            Assert.Contains("Roll the transaction back", refused.Message);
        });

        Assert.Equal(0, await host.InScopeAsync(db => db.Orders.CountAsync(Ct)));
    }

    [Fact]
    public async Task A_before_save_handler_that_ends_the_saves_own_transaction_and_goes_on_does_not_get_the_rows_written()
    {
        await using var host = await SavePipelineHost.StartOnSqlServerAsync("EndedBeforeTheWrite", services => services.AddScript(new Script<Order>
        {
            BeforeSave = async (rows, ct) =>
            {
                await rows.RequireTransactionAsync(ct);

                try
                {
                    await rows.Db.Database.ExecuteSqlRawAsync("SET XACT_ABORT ON; SELECT CAST('not a number' AS int);", ct);
                }
                catch (SqlException)
                {
                    // Not passed on.
                }
            },
        }));

        await host.InScopeAsync(async db =>
        {
            db.Add(new Order { Name = "on its own" });
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
            Assert.Contains("the transaction it is in has ended already", refused.Message);

            // Nothing was written, so the context is not left with rows it takes for saved.
            Assert.Equal(EntityState.Added, db.ChangeTracker.Entries<Order>().Single().State);
        });

        Assert.Equal(0, await host.InScopeAsync(db => db.Orders.CountAsync(Ct)));
    }

    [Fact]
    public async Task When_the_save_is_a_deadlock_victim_the_caller_sees_the_deadlock_and_the_transaction_is_rollback_only()
    {
        long a = 0, b = 0;
        Task<int>? other = null;
        SqlConnection? second = null;
        SqlTransaction? secondTransaction = null;

        await using var host = await SavePipelineHost.StartOnSqlServerAsync("Deadlock", services => services.AddScript(new Script<Order>
        {
            AfterSave = async (rows, ct) =>
            {
                if (rows.All(row => row.Entity.Name != "deadlocks"))
                    return;

                // This save takes memo A, then wants memo B. The other session holds B and is told to take A. The
                // priority is set in a batch of its own: inside a parameterized command it ends with that command.
                await rows.Db.Database.ExecuteSqlRawAsync("SET DEADLOCK_PRIORITY LOW;", ct);
                await rows.Db.Database.ExecuteSqlRawAsync("UPDATE [Memos] SET [Name] = 'ours' WHERE [ID] = {0}", [a], ct);

                var wantsA = new SqlCommand("UPDATE [Memos] SET [Name] = 'theirs' WHERE [ID] = @id", second, secondTransaction);
                wantsA.Parameters.AddWithValue("@id", a);
                other = wantsA.ExecuteNonQueryAsync(ct);

                await WaitUntilBlockedAsync(second!, ct);
                await rows.Db.Database.ExecuteSqlRawAsync("UPDATE [Memos] SET [Name] = 'ours' WHERE [ID] = {0}", [b], ct);
            },
            AfterCommit = Nothing,
        }));

        await using (var setup = host.Bare())
        {
            var memos = new[] { new Memo { Name = "a" }, new Memo { Name = "b" } };
            setup.AddRange(memos);
            await setup.SaveChangesAsync(Ct);
            (a, b) = (memos[0].ID, memos[1].ID);
        }

        await using (var bare = host.Bare())
            second = new SqlConnection(bare.Database.GetConnectionString());

        await using var _ = second;
        await second.OpenAsync(Ct);
        secondTransaction = second.BeginTransaction();

        await using (var priority = new SqlCommand("SET DEADLOCK_PRIORITY HIGH;", second, secondTransaction))
            await priority.ExecuteNonQueryAsync(Ct);

        await using (var holdsB = new SqlCommand("UPDATE [Memos] SET [Name] = 'theirs' WHERE [ID] = @id", second, secondTransaction))
        {
            holdsB.Parameters.AddWithValue("@id", b);
            await holdsB.ExecuteNonQueryAsync(Ct);
        }

        await host.InScopeAsync(async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);

            db.Add(new Order { Name = "first" });
            await db.SaveChangesAsync(Ct);

            db.Add(new Order { Name = "deadlocks" });
            var victim = await Assert.ThrowsAsync<SqlException>(() => db.SaveChangesAsync(Ct));
            Assert.Equal(1205, victim.Number);

            // The other session got its lock once this one was ended.
            Assert.Equal(1, await other!);

            Assert.Null(transaction.GetDbTransaction().Connection);

            var commit = await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.CommitAsync(Ct));
            Assert.Contains("can only be rolled back", commit.Message);
        });

        await secondTransaction.RollbackAsync(Ct);

        Assert.DoesNotContain(host.Log.Lines, line => line.StartsWith("after commit"));
        Assert.Equal(0, await host.InScopeAsync(db => db.Orders.CountAsync(Ct)));
    }

    // Waits until the request of another session waits for a lock.
    private static async Task WaitUntilBlockedAsync(SqlConnection blocked, CancellationToken cancellationToken)
    {
        await using var watcher = new SqlConnection(blocked.ConnectionString);
        await watcher.OpenAsync(cancellationToken);

        for (var attempt = 0; attempt < 200; attempt++)
        {
            await using var waiting = new SqlCommand(
                "SELECT COUNT(*) FROM sys.dm_exec_requests WHERE session_id = @session AND blocking_session_id <> 0", watcher);
            waiting.Parameters.AddWithValue("@session", blocked.ServerProcessId);

            if ((int)(await waiting.ExecuteScalarAsync(cancellationToken))! > 0)
                return;

            await Task.Delay(25, cancellationToken);
        }

        throw new TimeoutException("The other session never waited for the lock.");
    }
}
