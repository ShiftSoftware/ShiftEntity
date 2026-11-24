using System.Data;
using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.Tests.SaveHandlers;
using Xunit;
using static ShiftSoftware.ShiftEntity.Tests.SaveHandlers.SavePipelineModel;
using static ShiftSoftware.ShiftEntity.Tests.SaveLifecycle.ScriptedHandlers;
using IsolationLevel = System.Data.IsolationLevel;

namespace ShiftSoftware.ShiftEntity.Tests.SaveLifecycle;

/// <summary>
/// Which transaction a save runs in, and where its stages run: a save that needs none, one the context opens itself,
/// one the app began through <c>Database</c>, with and without an isolation level, and the two kinds the context cannot
/// follow. Each case runs on SQLite, and on SQL Server where the database's own behavior is the point.
/// </summary>
public class SaveTransactionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── No transaction, and the context's own ───────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_save_with_an_after_save_handler_runs_in_a_transaction_that_the_context_opens_and_commits(bool sqlServer)
    {
        var inside = "";

        await using var host = await SavePipelineHost.StartAsync(sqlServer, "OwnTransaction", services => services.AddScript(new Script<Order>
        {
            AfterSave = async (rows, ct) =>
                inside = $"{await rows.Db.Set<Memo>().CountAsync(ct)} memo(s), transaction open: {rows.Db.Database.CurrentTransaction is not null}",
        }));

        await host.InScopeAsync(async db =>
        {
            db.Add(new Order { Name = "one" });

            Assert.Equal(1, await db.SaveChangesAsync(Ct));
            Assert.Null(db.Database.CurrentTransaction);
        });

        // Begin, the write, the handler's SQL, commit.
        Assert.Equal(["BEGIN", "INSERT Orders", "SELECT", "COMMIT"], host.Sql.Lines);
        Assert.Equal("0 memo(s), transaction open: True", inside);
        Assert.Equal(1, await host.InScopeAsync(db => db.Orders.CountAsync(Ct)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_save_with_only_after_commit_handlers_opens_no_transaction(bool sqlServer)
    {
        var after = "";

        await using var host = await SavePipelineHost.StartAsync(sqlServer, "NoTransaction", services => services.AddScript(new Script<Order>
        {
            AfterCommit = async (rows, ct) =>
                after = $"{await rows.Db.Set<Order>().CountAsync(ct)} order(s), transaction open: {rows.Db.Database.CurrentTransaction is not null}",
        }));

        await host.InScopeAsync(async db =>
        {
            db.Add(new Order { Name = "one" });
            await db.SaveChangesAsync(Ct);
        });

        // The write is the commit, and the handler runs after it, inside the save call.
        Assert.Equal(["INSERT Orders", "SELECT"], host.Sql.Lines);
        Assert.Equal("1 order(s), transaction open: False", after);
    }

    [Fact]
    public async Task A_save_of_rows_that_no_handler_has_is_written_as_EF_writes_it()
    {
        await using var host = SavePipelineHost.Start(services => services.AddScript(new Script<Order>
        {
            AfterSave = Nothing,
            BeforeCommit = Nothing,
            AfterCommit = Nothing,
        }));

        await host.InScopeAsync(async db =>
        {
            db.Add(new Memo { Name = "no handler has memos" });
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(["INSERT Memos"], host.Sql.Lines);
        Assert.Empty(host.Log.Lines);
    }

    [Fact]
    public async Task A_before_commit_handler_makes_a_save_need_a_transaction_and_runs_once_before_its_commit()
    {
        await using var host = SavePipelineHost.Start(services => services.AddScript(new Script<Order>
        {
            BeforeCommit = async (rows, ct) =>
            {
                // Still inside the transaction: what it saves here commits with the rows, or not at all.
                rows.Db.Add(new Memo { Name = $"{rows.Count} order(s)" });
                await rows.Db.SaveChangesAsync(ct);
            },
        }));

        await host.InScopeAsync(async db =>
        {
            db.AddRange(new Order { Name = "a" }, new Order { Name = "b" });
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(["before commit of Order: Added Order 'a', Added Order 'b'"], host.Log.Lines);
        Assert.Equal("BEGIN", host.Sql.Writes[0]);
        Assert.Equal("COMMIT", host.Sql.Writes[^1]);
        Assert.Single(host.Sql.Writes, line => line == "BEGIN");
        Assert.Equal("2 order(s)", await host.InScopeAsync(db => db.Memos.Select(x => x.Name).SingleAsync(Ct)));
    }

    // ── The app's transaction ───────────────────────────────────────────────────

    private static Script<Order> EveryStage => new() { AfterSave = Nothing, BeforeCommit = Nothing, AfterCommit = Nothing };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_saves_of_a_transaction_begun_through_Database_share_one_before_commit_and_one_after_commit_inside_its_commit(bool sqlServer)
    {
        await using var host = await SavePipelineHost.StartAsync(sqlServer, "AppTransaction", services => services.AddScript(EveryStage));

        await host.InScopeAsync(async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            Assert.Same(transaction, db.Database.CurrentTransaction);

            var a = new Order { Name = "a" };
            db.Add(a);
            await db.SaveChangesAsync(Ct);

            a.Name = "a, renamed";
            db.Add(new Order { Name = "b" });
            await db.SaveChangesAsync(Ct);

            host.Log.Add("commit");
            await transaction.CommitAsync(Ct);
            host.Log.Add("committed");

            Assert.Null(db.Database.CurrentTransaction);
        });

        Assert.Equal(
        [
            "after save of Order: Added Order 'a'",
            "after save of Order: Added Order 'b', Modified Order 'a, renamed'",     // as EF accepted them: its added rows first
            "commit",
            "before commit of Order: Added Order 'a, renamed', Added Order 'b'",
            "after commit of Order: Added Order 'a, renamed', Added Order 'b'",
            "committed",
        ], host.Log.Lines);

        // One transaction, and one savepoint for each save: the framework's, in the place of EF's.
        Assert.Equal("BEGIN", host.Sql.Writes[0]);
        Assert.Equal("COMMIT", host.Sql.Writes[^1]);
        Assert.Equal(2, host.Sql.Writes.Count(line => line == "SAVEPOINT"));
        Assert.Equal(2, await host.InScopeAsync(db => db.Orders.CountAsync(Ct)));
    }

    [Theory]
    [InlineData("rollback")]
    [InlineData("dispose")]
    [InlineData("Database.RollbackTransaction")]
    public async Task A_transaction_that_ends_without_a_commit_runs_no_stage_for_what_it_saved(string ending)
    {
        await using var host = SavePipelineHost.Start(services => services.AddScript(EveryStage));

        await host.InScopeAsync(async db =>
        {
            await using (var transaction = await db.Database.BeginTransactionAsync(Ct))
            {
                db.Add(new Order { Name = "a" });
                await db.SaveChangesAsync(Ct);
                db.AfterCommit(() => host.Log.Add("queued work ran"));

                if (ending == "rollback")
                    await transaction.RollbackAsync(Ct);
                else if (ending == "Database.RollbackTransaction")
                    await db.Database.RollbackTransactionAsync(Ct);
            }

            Assert.Null(db.Database.CurrentTransaction);
        });

        Assert.Equal(["after save of Order: Added Order 'a'"], host.Log.Lines);
        Assert.Equal(0, await host.InScopeAsync(db => db.Orders.CountAsync(Ct)));
    }

    [Theory]
    [InlineData("Database.CommitTransaction")]
    [InlineData("Database.CommitTransactionAsync")]
    [InlineData("CurrentTransaction.Commit")]
    public async Task Every_way_to_commit_a_followed_transaction_runs_the_stages(string commit)
    {
        await using var host = SavePipelineHost.Start(services => services.AddScript(EveryStage));

        await host.InScopeAsync(async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            db.Add(new Order { Name = "a" });
            await db.SaveChangesAsync(Ct);

            if (commit == "Database.CommitTransaction")
                db.Database.CommitTransaction();
            else if (commit == "Database.CommitTransactionAsync")
                await db.Database.CommitTransactionAsync(Ct);
            else
                db.Database.CurrentTransaction!.Commit();
        });

        Assert.Equal(
        [
            "after save of Order: Added Order 'a'",
            "before commit of Order: Added Order 'a'",
            "after commit of Order: Added Order 'a'",
        ], host.Log.Lines);
    }

    // ── With an isolation level ─────────────────────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_transaction_begun_with_an_isolation_level_through_the_context_type_is_followed_and_has_that_level(bool sqlServer)
    {
        await using var host = await SavePipelineHost.StartAsync(sqlServer, "IsolationLevel", services => services.AddScript(EveryStage));

        await host.InScopeAsync(async db =>
        {
            await using (var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, Ct))
            {
                Assert.Equal(IsolationLevel.Serializable, transaction.GetDbTransaction().IsolationLevel);
                Assert.Same(transaction, db.Database.CurrentTransaction);

                db.Add(new Order { Name = "async" });
                await db.SaveChangesAsync(Ct);
                await transaction.CommitAsync(Ct);
            }

            using (var transaction = db.Database.BeginTransaction(IsolationLevel.Serializable))
            {
                Assert.Equal(IsolationLevel.Serializable, transaction.GetDbTransaction().IsolationLevel);

                db.Add(new Order { Name = "sync" });
                db.SaveChanges();
                transaction.Commit();
            }
        });

        Assert.Equal(
        [
            "after save of Order: Added Order 'async'", "before commit of Order: Added Order 'async'", "after commit of Order: Added Order 'async'",
            "after save of Order: Added Order 'sync'", "before commit of Order: Added Order 'sync'", "after commit of Order: Added Order 'sync'",
        ], host.Log.Lines);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Through_a_variable_typed_DbContext_the_isolation_level_overload_is_EFs_own_and_the_transaction_is_not_followed(bool sqlServer)
    {
        // An assembly compiled against an older framework makes exactly these calls: it reads Database as EF declares
        // it, and binds BeginTransaction(IsolationLevel) to EF's extension method.
        await using var host = await SavePipelineHost.StartAsync(sqlServer, "PlainDbContext", services => services.AddScript(new Script<Order>
        {
            AfterCommit = Nothing,
        }));

        await host.InScopeAsync(async db =>
        {
            DbContext plain = db;

            // Without an isolation level the call is virtual, so it reaches the context's facade and is followed.
            await using (var followed = await plain.Database.BeginTransactionAsync(Ct))
            {
                db.Add(new Order { Name = "followed" });
                await db.SaveChangesAsync(Ct);
                Assert.Empty(host.Log.Lines);

                await followed.CommitAsync(Ct);
                Assert.Equal(["after commit of Order: Added Order 'followed'"], host.Log.Lines);
            }

            await using (var unseen = await plain.Database.BeginTransactionAsync(IsolationLevel.Serializable, Ct))
            {
                Assert.Equal(IsolationLevel.Serializable, unseen.GetDbTransaction().IsolationLevel);
                Assert.Same(unseen, db.Database.CurrentTransaction);

                var order = new Order { Name = "refused" };
                db.Add(order);
                host.Sql.Clear();

                var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
                Assert.Contains("cannot save Order rows, which have a before-commit or an after-commit handler", refused.Message);
                Assert.Contains("BeginTransaction(IsolationLevel) through a variable typed DbContext", refused.Message);
                Assert.Empty(host.Sql.Lines);

                // A memo has no handler, so nothing waits for the commit, and it is saved in that transaction.
                db.Entry(order).State = EntityState.Detached;
                db.Add(new Memo { Name = "saved" });
                await db.SaveChangesAsync(Ct);

                await unseen.CommitAsync(Ct);
            }
        });

        Assert.Equal(["after commit of Order: Added Order 'followed'"], host.Log.Lines);
        Assert.Equal(1, await host.InScopeAsync(db => db.Memos.CountAsync(Ct)));
    }

    // ── Transactions the context cannot follow ──────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Inside_a_UseTransaction_transaction_a_save_with_work_that_waits_for_the_commit_is_refused_before_it_writes(bool sqlServer)
    {
        await using var host = await SavePipelineHost.StartAsync(sqlServer, "UseTransactionRefused", services => services.AddScript(new Script<Order>
        {
            AfterCommit = Nothing,
        }));

        await host.InScopeAsync(async db =>
        {
            await db.Database.OpenConnectionAsync(Ct);
            await using var owner = await db.Database.GetDbConnection().BeginTransactionAsync(Ct);
            var joined = await db.Database.UseTransactionAsync(owner, Ct);

            // EF's own object: the owner commits, and the context is not told.
            Assert.Same(joined, db.Database.CurrentTransaction);

            db.Add(new Order { Name = "refused" });
            host.Sql.Clear();

            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
            Assert.Contains("cannot save Order rows, which have a before-commit or an after-commit handler", refused.Message);
            Assert.Contains("UseTransaction", refused.Message);
            Assert.Contains("Begin the transaction through this context's Database", refused.Message);
            Assert.Empty(host.Sql.Lines);

            // Work queued in code cannot wait there either.
            var work = Assert.Throws<InvalidOperationException>(() => db.AfterCommit(() => host.Log.Add("ran")));
            Assert.Contains("cannot hold work for after the commit", work.Message);

            await owner.CommitAsync(Ct);
            await db.Database.UseTransactionAsync(null, Ct);
        });

        Assert.Empty(host.Log.Lines);
        Assert.Equal(0, await host.InScopeAsync(db => db.Orders.CountAsync(Ct)));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task Inside_a_UseTransaction_transaction_after_save_runs_in_it_and_is_atomic_through_it(bool sqlServer, bool commit)
    {
        await using var host = await SavePipelineHost.StartAsync(sqlServer, "UseTransactionAfterSave", services => services.AddScript(new Script<Order>
        {
            AfterSave = async (rows, ct) =>
            {
                rows.Db.Add(new Memo { Name = "written with the order" });
                await rows.Db.SaveChangesAsync(ct);
            },
        }));

        await host.InScopeAsync(async db =>
        {
            await db.Database.OpenConnectionAsync(Ct);
            await using var owner = await db.Database.GetDbConnection().BeginTransactionAsync(Ct);
            await db.Database.UseTransactionAsync(owner, Ct);

            db.Add(new Order { Name = "a" });
            await db.SaveChangesAsync(Ct);

            // No transaction of the context's own: the owner's holds the save.
            Assert.DoesNotContain("BEGIN", host.Sql.Writes);

            if (commit)
                await owner.CommitAsync(Ct);
            else
                await owner.RollbackAsync(Ct);

            await db.Database.UseTransactionAsync(null, Ct);
        });

        Assert.Equal(["after save of Order: Added Order 'a'"], host.Log.Lines);

        await using var bare = host.Bare();
        Assert.Equal(commit ? (1, 1) : (0, 0), (await bare.Orders.CountAsync(Ct), await bare.Memos.CountAsync(Ct)));
    }

    [Fact]
    public async Task Inside_a_TransactionScope_a_save_with_work_that_waits_for_the_commit_is_refused_before_it_writes()
    {
        await using var host = await SavePipelineHost.StartOnSqlServerAsync("ScopeRefused", services => services.AddScript(new Script<Order>
        {
            BeforeCommit = Nothing,
        }));

        using (var ambient = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            await host.InScopeAsync(async db =>
            {
                Assert.Null(db.Database.CurrentTransaction);

                db.Add(new Order { Name = "refused" });

                var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
                Assert.Contains("cannot save Order rows, which have a before-commit or an after-commit handler", refused.Message);
                Assert.Contains("an ambient transaction (TransactionScope)", refused.Message);
                Assert.Empty(host.Sql.Lines);

                // EF itself refuses a transaction of the context's inside a scope.
                await Assert.ThrowsAsync<InvalidOperationException>(() => db.Database.BeginTransactionAsync(Ct));
            });

            ambient.Complete();
        }

        Assert.Empty(host.Log.Lines);
        Assert.Equal(0, await host.InScopeAsync(db => db.Orders.CountAsync(Ct)));
    }

    [Fact]
    public async Task On_SQLite_too_a_TransactionScope_is_a_transaction_the_context_cannot_follow()
    {
        // SQLite takes no part in an ambient transaction, and EF still reports one. The refusal is the same as on SQL
        // Server, so a host's tests on SQLite fail where its production database would.
        await using var host = SavePipelineHost.Start(services => services.AddScript(new Script<Order> { AfterCommit = Nothing }));

        using var ambient = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);

        await host.InScopeAsync(async db =>
        {
            db.Add(new Order { Name = "refused" });

            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
            Assert.Contains("an ambient transaction (TransactionScope)", refused.Message);
            Assert.Empty(host.Sql.Lines);
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Inside_a_TransactionScope_after_save_runs_in_it_and_is_atomic_through_it(bool complete)
    {
        await using var host = await SavePipelineHost.StartOnSqlServerAsync("ScopeAfterSave", services => services.AddScript(new Script<Order>
        {
            AfterSave = async (rows, ct) =>
            {
                rows.Db.Add(new Memo { Name = "written with the order" });
                await rows.Db.SaveChangesAsync(ct);
            },
        }));

        using (var ambient = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            await host.InScopeAsync(async db =>
            {
                db.Add(new Order { Name = "a" });
                await db.SaveChangesAsync(Ct);
            });

            // The scope holds the save: the context opened no transaction of its own.
            Assert.DoesNotContain("BEGIN", host.Sql.Writes);

            if (complete)
                ambient.Complete();
        }

        Assert.Equal(["after save of Order: Added Order 'a'"], host.Log.Lines);

        await using var bare = host.Bare();
        Assert.Equal(complete ? (1, 1) : (0, 0), (await bare.Orders.CountAsync(Ct), await bare.Memos.CountAsync(Ct)));
    }

    [Fact]
    public async Task Inside_a_TransactionScope_a_save_that_fails_after_its_write_rolls_the_scope_back()
    {
        // No savepoint can be set in an ambient transaction, so the failed save cannot be undone alone. The context
        // rolls the ambient transaction back, and the scope can no longer commit.
        await using var host = await SavePipelineHost.StartOnSqlServerAsync("ScopeFailure", services => services.AddScript(new Script<Order>
        {
            AfterSave = (rows, _) => rows.Any(row => row.Entity.Name == "fails") ? throw new InvalidOperationException("The handler failed.") : Task.CompletedTask,
        }));

        var aborted = await Assert.ThrowsAsync<TransactionAbortedException>(async () =>
        {
            using var ambient = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);

            await host.InScopeAsync(async db =>
            {
                db.Add(new Order { Name = "first" });
                await db.SaveChangesAsync(Ct);

                db.Add(new Order { Name = "fails" });
                var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
                Assert.Equal("The handler failed.", failure.Message);
            });

            // The caller swallowed the failure and tries to commit all the same.
            ambient.Complete();
        });

        Assert.NotNull(aborted);
        Assert.Equal(0, await host.InScopeAsync(db => db.Orders.CountAsync(Ct)));
    }

    // ── Savepoints ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_save_with_nothing_after_its_write_keeps_EFs_own_savepoint(bool sqlServer)
    {
        await using var host = await SavePipelineHost.StartAsync(sqlServer, "EfSavepoint");

        await host.InScopeAsync(async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            db.Add(new Memo { Name = "a" });
            await db.SaveChangesAsync(Ct);
            await transaction.CommitAsync(Ct);
        });

        Assert.Equal(["BEGIN", "SAVEPOINT", "INSERT Memos", "COMMIT"], host.Sql.Writes);
    }

    [Fact]
    public async Task A_save_with_nothing_to_write_sets_no_savepoint_and_sends_nothing()
    {
        // In a host where every save has something after its write: an observer, and handlers.
        await using var host = SavePipelineHost.Start(services => services
            .AddScript(EveryStage)
            .AddScoped<IShiftDbContextSaveObserver, LoggingSaveObserver>());

        await host.InScopeAsync(async db =>
        {
            Assert.Equal(0, await db.SaveChangesAsync(Ct));

            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            Assert.Equal(0, await db.SaveChangesAsync(Ct));
            Assert.Equal(0, db.SaveChanges());
            await transaction.CommitAsync(Ct);
        });

        Assert.Equal(["BEGIN", "COMMIT"], host.Sql.Lines);
        Assert.Empty(host.Log.Lines);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_frameworks_savepoint_takes_the_place_of_EFs_and_undoes_a_save_that_fails_after_its_write(bool sqlServer)
    {
        await using var host = await SavePipelineHost.StartAsync(sqlServer, "FrameworkSavepoint", services => services.AddScript(new Script<Order>
        {
            AfterSave = async (rows, ct) =>
            {
                // What the stage itself writes is undone with the save.
                rows.Db.Add(new Memo { Name = "memo of " + rows[0].Entity.Name });
                await rows.Db.SaveChangesAsync(ct);

                if (rows.Any(row => row.Entity.Name == "fails"))
                    throw new InvalidOperationException("The handler failed.");
            },
            AfterCommit = Nothing,
        }));

        await host.InScopeAsync(async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);

            db.Add(new Order { Name = "kept" });
            await db.SaveChangesAsync(Ct);

            var failing = new Order { Name = "fails" };
            db.Add(failing);
            host.Sql.Clear();

            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
            Assert.Equal("The handler failed.", failure.Message);

            // One savepoint for the save, as with EF's own, one more for the save the handler made, and the rollback.
            Assert.Equal(["SAVEPOINT", "INSERT Orders", "SAVEPOINT", "INSERT Memos", "ROLLBACK TO SAVEPOINT"], host.Sql.Writes);

            // EF had accepted the row, so the context shows it as saved. It refuses to go on until it is cleared.
            Assert.Equal(EntityState.Unchanged, db.Entry(failing).State);
            var stale = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
            Assert.Contains("holds rows of a save that was rolled back", stale.Message);
            Assert.Contains("ChangeTracker.Clear()", stale.Message);

            db.ChangeTracker.Clear();
            await db.SaveChangesAsync(Ct);

            // The transaction is still usable, and the earlier save is still in it.
            await transaction.CommitAsync(Ct);
        });

        Assert.Equal(
        [
            "after save of Order: Added Order 'kept'",
            "after save of Order: Added Order 'fails'",
            "after commit of Order: Added Order 'kept'",
        ], host.Log.Lines);

        await using var bare = host.Bare();
        Assert.Equal(["kept"], await bare.Orders.Select(x => x.Name).ToListAsync(Ct));
        Assert.Equal(["memo of kept"], await bare.Memos.Select(x => x.Name).ToListAsync(Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Two_contexts_in_one_transaction_keep_their_savepoints_apart(bool sqlServer)
    {
        SavePipelineHost? running = null;

        await using var host = running = await SavePipelineHost.StartAsync(sqlServer, "TwoContexts", services => services
            .AddScript(new Script<Order>
            {
                AfterSave = async (rows, ct) =>
                {
                    if (rows[0].Entity.Name != "fails")
                        return;

                    // A second context saves in the same transaction. That save is the outermost one of its context,
                    // as the failing save is of this one.
                    await using var scope = running!.Provider.CreateAsyncScope();
                    await using var second = JoinedTo(rows.Db, scope.ServiceProvider, sqlServer);
                    second.Add(new Memo { Name = "saved by the second context" });
                    await second.SaveChangesAsync(ct);

                    throw new InvalidOperationException("The handler failed.");
                },
            })
            .AddScript(new Script<Memo> { AfterSave = Nothing }));

        await host.InScopeAsync(async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);

            db.Add(new Order { Name = "kept" });
            await db.SaveChangesAsync(Ct);

            db.Add(new Order { Name = "fails" });
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
            Assert.Equal("The handler failed.", failure.Message);

            db.ChangeTracker.Clear();
            await transaction.CommitAsync(Ct);
        });

        Assert.Contains("after save of Memo: Added Memo 'saved by the second context'", host.Log.Lines);

        // The second context set its savepoint after the failing save's write, and SQL Server keeps a savepoint until
        // the transaction ends. Under one name for both, the rollback would have stopped there and kept the order.
        await using var bare = host.Bare();
        Assert.Equal(["kept"], await bare.Orders.Select(x => x.Name).ToListAsync(Ct));
        Assert.Equal(0, await bare.Memos.CountAsync(Ct));
    }

    // A context of the same host on another context's connection, joined to its transaction.
    private static Db JoinedTo(ShiftDbContext first, IServiceProvider services, bool sqlServer)
    {
        var options = new DbContextOptionsBuilder<Db>().UseApplicationServiceProvider(services);
        var connection = first.Database.GetDbConnection();

        if (sqlServer)
            options.UseSqlServer(connection);
        else
            options.UseSqlite(connection);

        var second = new Db(options.Options);
        second.Database.UseTransaction(first.Database.CurrentTransaction!.GetDbTransaction());
        return second;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_rollback_to_a_savepoint_of_the_apps_own_drops_what_the_saves_after_it_had_waiting(bool sqlServer)
    {
        await using var host = await SavePipelineHost.StartAsync(sqlServer, "AppSavepoint", services => services.AddScript(new Script<Order>
        {
            AfterCommit = Nothing,
        }));

        await host.InScopeAsync(async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);

            db.Add(new Order { Name = "before the savepoint" });
            await db.SaveChangesAsync(Ct);
            db.AfterCommit(() => host.Log.Add("work queued before the savepoint"));

            await transaction.CreateSavepointAsync("mine", Ct);

            db.Add(new Order { Name = "after the savepoint" });
            await db.SaveChangesAsync(Ct);
            db.AfterCommit(() => host.Log.Add("work queued after the savepoint"));

            await transaction.RollbackToSavepointAsync("mine", Ct);

            // The context shows the undone row as saved, so it must be cleared before it saves again.
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
            db.ChangeTracker.Clear();

            db.Add(new Order { Name = "after the rollback" });
            await db.SaveChangesAsync(Ct);

            await transaction.CommitAsync(Ct);
        });

        Assert.Equal(
        [
            "work queued before the savepoint",
            "after commit of Order: Added Order 'before the savepoint', Added Order 'after the rollback'",
        ], host.Log.Lines);

        Assert.Equal(["before the savepoint", "after the rollback"],
            await host.InScopeAsync(db => db.Orders.OrderBy(x => x.ID).Select(x => x.Name).ToListAsync(Ct)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task With_savepoints_switched_off_a_save_that_fails_after_its_write_leaves_a_transaction_that_can_only_be_rolled_back(bool sqlServer)
    {
        await using var host = await SavePipelineHost.StartAsync(sqlServer, "NoSavepoints", services => services.AddScript(new Script<Order>
        {
            AfterSave = (rows, _) => rows.Any(row => row.Entity.Name == "fails") ? throw new InvalidOperationException("The handler failed.") : Task.CompletedTask,
            AfterCommit = Nothing,
        }));

        await host.InScopeAsync(async db =>
        {
            db.Database.AutoSavepointsEnabled = false;
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);

            db.Add(new Order { Name = "first" });
            await db.SaveChangesAsync(Ct);

            db.Add(new Order { Name = "fails" });
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
            Assert.DoesNotContain("SAVEPOINT", host.Sql.Writes);

            var commit = await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.CommitAsync(Ct));
            Assert.Contains("can only be rolled back", commit.Message);
            Assert.Contains("without a savepoint", commit.Message);

            await transaction.RollbackAsync(Ct);
        });

        Assert.DoesNotContain(host.Log.Lines, line => line.StartsWith("after commit"));
        Assert.Equal(0, await host.InScopeAsync(db => db.Orders.CountAsync(Ct)));
    }

    // ── From inside a stage ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("after save")]
    [InlineData("before commit")]
    public async Task Beginning_a_transaction_from_inside_a_stage_before_the_commit_is_refused(string stage)
    {
        static async Task Begin(SavedRows<Order> rows, CancellationToken ct) => await rows.Db.Database.BeginTransactionAsync(ct);

        await using var host = SavePipelineHost.Start(services => services.AddScript(
            stage == "after save" ? new Script<Order> { AfterSave = Begin } : new Script<Order> { BeforeCommit = Begin }));

        await host.InScopeAsync(async db =>
        {
            db.Add(new Order { Name = "a" });

            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
            Assert.Contains("cannot begin a transaction from inside a save", refused.Message);
        });

        // The refusal failed the save, which was undone.
        Assert.Equal(0, await host.InScopeAsync(db => db.Orders.CountAsync(Ct)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task After_the_commit_a_handler_may_begin_a_transaction_of_its_own_and_queue_work_in_it(bool theSaveHadATransaction)
    {
        SaveLog? log = null;

        await using var host = SavePipelineHost.Start(services => services.AddScript(new Script<Order>
        {
            // With an after-save handler the context opens a transaction for the save, and commits it before this stage.
            AfterSave = theSaveHadATransaction ? Nothing : null,
            AfterCommit = async (rows, ct) =>
            {
                await using var transaction = await rows.Db.Database.BeginTransactionAsync(ct);

                rows.Db.Add(new Memo { Name = "memo of " + rows[0].Entity.Name });
                await rows.Db.SaveChangesAsync(ct);
                rows.Db.AfterCommit(() => log!.Add("the work queued in the handler's transaction"));

                log!.Add("the handler commits");
                await transaction.CommitAsync(ct);
                log.Add("the handler committed");
            },
        }));

        log = host.Log;

        await host.InScopeAsync(async db =>
        {
            db.Add(new Order { Name = "a" });
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(
        [
            "after commit of Order: Added Order 'a'",
            "the handler commits",
            "the work queued in the handler's transaction",
            "the handler committed",
        ], host.Log.Lines.Where(line => !line.StartsWith("after save")));

        Assert.Equal("memo of a", await host.InScopeAsync(db => db.Memos.Select(x => x.Name).SingleAsync(Ct)));
    }

    [Theory]
    [InlineData("after save", "committed", false)]
    [InlineData("after save", "rolled back", false)]
    [InlineData("after save", "disposed", false)]
    [InlineData("after save", "committed", true)]
    [InlineData("after save", "rolled back", true)]
    [InlineData("after save", "disposed", true)]
    [InlineData("before commit", "committed", false)]
    [InlineData("before commit", "rolled back", true)]
    public async Task The_transaction_a_save_runs_in_cannot_be_ended_from_inside_a_stage(string stage, string ended, bool appBeganIt)
    {
        async Task End(SavedRows<Order> rows, CancellationToken ct)
        {
            var transaction = rows.Db.Database.CurrentTransaction!;

            if (ended == "committed")
                await transaction.CommitAsync(ct);
            else if (ended == "rolled back")
                await transaction.RollbackAsync(ct);
            else
                await transaction.DisposeAsync();
        }

        await using var host = SavePipelineHost.Start(services => services.AddScript(
            stage == "after save" ? new Script<Order> { AfterSave = End } : new Script<Order> { BeforeCommit = End }));

        await host.InScopeAsync(async db =>
        {
            await using var transaction = appBeganIt ? await db.Database.BeginTransactionAsync(Ct) : null;

            db.Add(new Order { Name = "a" });

            var refused = stage == "before commit" && appBeganIt
                ? await SaveAndThen(db, () => Assert.ThrowsAsync<InvalidOperationException>(() => transaction!.CommitAsync(Ct)))
                : await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));

            Assert.Contains($"cannot be {ended} from inside a save that runs in it", refused.Message);
        });

        Assert.Equal(0, await host.InScopeAsync(db => db.Orders.CountAsync(Ct)));
    }

    [Fact]
    public async Task A_stage_may_roll_back_to_a_savepoint_it_set_itself_and_not_to_one_that_was_set_before_its_save()
    {
        await using var host = SavePipelineHost.Start(services => services.AddScript(new Script<Order>
        {
            AfterSave = async (rows, ct) =>
            {
                var transaction = rows.Db.Database.CurrentTransaction!;

                if (rows[0].Entity.Name == "rolls back to the first")
                {
                    await transaction.RollbackToSavepointAsync("first", ct);
                    return;
                }

                // A try of its own inside the stage: saved, and taken back.
                await transaction.CreateSavepointAsync("inside the stage", ct);
                var memo = new Memo { Name = "tried by the handler" };
                rows.Db.Add(memo);
                await rows.Db.SaveChangesAsync(ct);
                await transaction.RollbackToSavepointAsync("inside the stage", ct);
                rows.Db.Entry(memo).State = EntityState.Detached;
            },
        }));

        await host.InScopeAsync(async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            await transaction.CreateSavepointAsync("first", Ct);

            db.Add(new Order { Name = "rolls back to its own" });
            await db.SaveChangesAsync(Ct);

            // The first savepoint holds none of this save: rolling back to it would take the save's write from under it.
            db.Add(new Order { Name = "rolls back to the first" });
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
            Assert.Contains("cannot be rolled back to the savepoint 'first' from inside a save that runs in it", refused.Message);

            db.ChangeTracker.Clear();
            await transaction.CommitAsync(Ct);
        });

        await using var bare = host.Bare();
        Assert.Equal(["rolls back to its own"], await bare.Orders.Select(x => x.Name).ToListAsync(Ct));
        Assert.Equal(0, await bare.Memos.CountAsync(Ct));
    }

    [Fact]
    public async Task Inside_a_transaction_the_context_cannot_follow_a_stage_cannot_end_it_through_Database_either()
    {
        await using var host = SavePipelineHost.Start(services => services.AddScript(new Script<Order>
        {
            AfterSave = async (rows, ct) => await rows.Db.Database.RollbackTransactionAsync(ct),
        }));

        await host.InScopeAsync(async db =>
        {
            await db.Database.OpenConnectionAsync(Ct);
            await using var owner = await db.Database.GetDbConnection().BeginTransactionAsync(Ct);
            await db.Database.UseTransactionAsync(owner, Ct);

            db.Add(new Order { Name = "a" });
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
            Assert.Contains("cannot be rolled back from inside that save", refused.Message);

            // The owner's transaction is still there, and the failed save was undone in it.
            await owner.CommitAsync(Ct);
            await db.Database.UseTransactionAsync(null, Ct);
        });

        Assert.Equal(0, await host.InScopeAsync(db => db.Orders.CountAsync(Ct)));
    }

    [Fact]
    public async Task A_commit_is_refused_when_a_save_made_before_commit_failed_and_could_not_be_undone_alone()
    {
        await using var host = SavePipelineHost.Start(services => services
            .AddScript(new Script<Order>
            {
                BeforeCommit = async (rows, ct) =>
                {
                    rows.Db.Add(new Memo { Name = "its handler fails" });

                    try
                    {
                        await rows.Db.SaveChangesAsync(ct);
                    }
                    catch (InvalidOperationException)
                    {
                        // The handler carries on as if nothing had happened.
                    }
                },
            })
            .AddScript(new Script<Memo> { AfterSave = (_, _) => throw new InvalidOperationException("The handler of the memo failed.") }));

        await host.InScopeAsync(async db =>
        {
            db.Database.AutoSavepointsEnabled = false;
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);

            db.Add(new Order { Name = "a" });
            await db.SaveChangesAsync(Ct);

            // The memo is still in the transaction, with its after-save work not done.
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.CommitAsync(Ct));
            Assert.Contains("can only be rolled back", refused.Message);
        });

        Assert.Equal((0, 0), await host.InScopeAsync(async db => (await db.Orders.CountAsync(Ct), await db.Memos.CountAsync(Ct))));
    }

    private static async Task<T> SaveAndThen<T>(Db db, Func<Task<T>> then)
    {
        await db.SaveChangesAsync(Ct);
        return await then();
    }

    [Fact]
    public async Task A_second_transaction_while_one_is_open_throws_as_EF_always_did()
    {
        await using var host = SavePipelineHost.Start();

        await host.InScopeAsync(async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);

            var second = await Assert.ThrowsAsync<InvalidOperationException>(() => db.Database.BeginTransactionAsync(Ct));
            Assert.Contains("already in a transaction", second.Message);

            // The first one is still the followed one.
            Assert.Same(transaction, db.Database.CurrentTransaction);
            await transaction.CommitAsync(Ct);
        });
    }
}
