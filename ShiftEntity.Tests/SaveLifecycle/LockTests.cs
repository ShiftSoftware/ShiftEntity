using System.Data;
using System.Transactions;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.Tests.Replication;
using ShiftSoftware.ShiftEntity.Tests.SaveHandlers;
using Xunit;
using static ShiftSoftware.ShiftEntity.Tests.SaveHandlers.SavePipelineModel;
using static ShiftSoftware.ShiftEntity.Tests.SaveLifecycle.ScriptedHandlers;
using IsolationLevel = System.Data.IsolationLevel;

namespace ShiftSoftware.ShiftEntity.Tests.SaveLifecycle;

/// <summary>
/// The lock on a name that a transaction holds until it ends: through <c>Database.TakeLockAsync</c> and through a
/// handler's rows, on SQL Server, on SQLite and where there is nothing to lock with. What a handler that numbers rows
/// under one gives out is in <see cref="BeforeSaveTests"/>.
/// </summary>
public class LockTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Whether another transaction gets the lock now. It asks for half a second.
    private static Task<bool> IsFreeAsync(SavePipelineHost host, string name) => host.InScopeAsync(async db =>
    {
        db.Database.SetCommandTimeout(1);
        await using var transaction = await db.Database.BeginTransactionAsync(Ct);

        try
        {
            await db.Database.TakeLockAsync(name, Ct);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    });

    // ── On SQL Server ───────────────────────────────────────────────────────────

    [Fact]
    public async Task A_transaction_holds_a_lock_until_it_commits_is_rolled_back_or_is_disposed()
    {
        await using var host = await SavePipelineHost.StartOnSqlServerAsync("LockUntil");

        Func<IDbContextTransaction, Task>[] endings = [x => x.CommitAsync(Ct), x => x.RollbackAsync(Ct), _ => Task.CompletedTask];

        foreach (var end in endings)
        {
            await host.InScopeAsync(async db =>
            {
                await using (var transaction = await db.Database.BeginTransactionAsync(Ct))
                {
                    await db.Database.TakeLockAsync("a", Ct);

                    // A save in between does not release it.
                    db.Add(new Order { Name = "a" });
                    await db.SaveChangesAsync(Ct);

                    Assert.False(await IsFreeAsync(host, "a"));
                    await end(transaction);
                }

                Assert.True(await IsFreeAsync(host, "a"));
            });
        }
    }

    [Fact]
    public async Task Another_transaction_waits_for_a_lock_and_gets_it_when_it_is_released()
    {
        await using var host = await SavePipelineHost.StartOnSqlServerAsync("LockWaits");

        await host.InScopeAsync(first => host.InScopeAsync(async second =>
        {
            await using var firsts = await first.Database.BeginTransactionAsync(Ct);
            await using var seconds = await second.Database.BeginTransactionAsync(Ct);

            await first.Database.TakeLockAsync("a", Ct);
            var waiting = second.Database.TakeLockAsync("a", Ct);

            await Task.Delay(300, Ct);
            Assert.False(waiting.IsCompleted);

            await firsts.CommitAsync(Ct);
            await waiting.WaitAsync(TimeSpan.FromSeconds(20), Ct);

            Assert.False(await IsFreeAsync(host, "a"));
        }));
    }

    [Fact]
    public async Task A_lock_is_on_its_exact_name_and_a_transaction_may_take_it_again()
    {
        await using var host = await SavePipelineHost.StartOnSqlServerAsync("LockNames");
        var longest = new string('n', 255);

        await host.InScopeAsync(async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);

            await db.Database.TakeLockAsync("Orders", Ct);
            await db.Database.TakeLockAsync("Orders", Ct);
            await db.Database.TakeLockAsync(longest, Ct);

            Assert.False(await IsFreeAsync(host, "Orders"));
            Assert.True(await IsFreeAsync(host, "orders"));
            Assert.True(await IsFreeAsync(host, "Orders:1"));
            Assert.False(await IsFreeAsync(host, longest));
            Assert.True(await IsFreeAsync(host, longest[..254] + "m"));
        });

        // Both requests for it ended with the transaction.
        Assert.True(await IsFreeAsync(host, "Orders"));
    }

    [Fact]
    public async Task Code_that_calls_sp_getapplock_itself_with_the_name_is_held_back_too()
    {
        // So an app can go over to TakeLockAsync while its other build, or a script, still uses the procedure.
        await using var host = await SavePipelineHost.StartOnSqlServerAsync("LockInterop");

        static Task<int> Raw(Db db, string owner) => db.Database.ExecuteSqlRawAsync(
            "DECLARE @result int; EXEC @result = sp_getapplock @Resource = 'a', @LockMode = 'Exclusive', @LockOwner = {0}, @LockTimeout = 0; " +
            "IF @result < 0 THROW 50000, 'taken', 1;", [owner], Ct);

        await host.InScopeAsync(async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            await db.Database.TakeLockAsync("a", Ct);

            var held = await Assert.ThrowsAsync<SqlException>(() => host.InScopeAsync(other => Raw(other, "Session")));
            Assert.Contains("taken", held.Message);
        });

        await host.InScopeAsync(async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            await Raw(db, "Transaction");

            Assert.False(await IsFreeAsync(host, "a"));
        });
    }

    // ── A wait that does not end with the lock ──────────────────────────────────

    [Fact]
    public async Task A_wait_that_is_too_long_ends_with_a_timeout_and_the_transaction_can_go_on()
    {
        await using var host = await SavePipelineHost.StartOnSqlServerAsync("LockTimeout");

        await host.InScopeAsync(first => host.InScopeAsync(async second =>
        {
            await using var firsts = await first.Database.BeginTransactionAsync(Ct);
            await using var seconds = await second.Database.BeginTransactionAsync(Ct);
            await first.Database.TakeLockAsync("a", Ct);

            second.Database.SetCommandTimeout(2);
            var timeout = await Assert.ThrowsAsync<TimeoutException>(() => second.Database.TakeLockAsync("a", Ct));
            Assert.Contains("The lock 'a' could not be taken within 1 s, because another transaction holds it.", timeout.Message);

            // Nothing was lost: the second transaction takes another lock, then this one, and saves.
            await second.Database.TakeLockAsync("b", Ct);
            await firsts.CommitAsync(Ct);
            await second.Database.TakeLockAsync("a", Ct);

            second.Add(new Order { Name = "saved" });
            await second.SaveChangesAsync(Ct);
            await seconds.CommitAsync(Ct);
        }));

        Assert.Equal(["saved"], await host.InScopeAsync(db => db.Orders.Select(x => x.Name).ToListAsync(Ct)));
    }

    [Theory]
    [InlineData(null, null, 29_000)]
    [InlineData(null, 5, 4_000)]
    [InlineData(60, null, 59_000)]
    [InlineData(60, 1, 500)]
    [InlineData(60, 0, -1)]
    [InlineData(null, int.MaxValue, int.MaxValue)]
    public async Task The_wait_is_a_little_less_than_a_command_of_the_context_may_take(int? ofTheConnection, int? ofTheContext, int milliseconds)
    {
        // The connection is not opened: only the settings are read.
        var connectionString = @"Server=.\NOBODY;Database=x;Encrypt=false" + (ofTheConnection is { } seconds ? $";Command Timeout={seconds}" : "");
        await using var db = new Db(new DbContextOptionsBuilder<Db>().UseSqlServer(connectionString).Options);

        if (ofTheContext is { } own)
            db.Database.SetCommandTimeout(own);

        Assert.Equal(milliseconds, DatabaseLock.MillisecondsToWait(db.Database));
    }

    [Fact]
    public async Task A_wait_that_is_cancelled_ends_with_an_OperationCanceledException_and_the_transaction_can_go_on()
    {
        await using var host = await SavePipelineHost.StartOnSqlServerAsync("LockCancelled");

        await host.InScopeAsync(first => host.InScopeAsync(async second =>
        {
            await using var firsts = await first.Database.BeginTransactionAsync(Ct);
            await using var seconds = await second.Database.BeginTransactionAsync(Ct);
            await first.Database.TakeLockAsync("a", Ct);

            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            var waiting = second.Database.TakeLockAsync("a", cancel.Token);
            await Task.Delay(300, Ct);
            await cancel.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(20), Ct));

            await firsts.CommitAsync(Ct);
            await second.Database.TakeLockAsync("a", Ct);
            Assert.False(await IsFreeAsync(host, "a"));
        }));
    }

    [Fact]
    public async Task Of_two_transactions_that_wait_for_each_other_one_is_told_to_roll_back_and_the_other_goes_on()
    {
        await using var host = await SavePipelineHost.StartOnSqlServerAsync("LockDeadlock");

        async Task<Exception?> TakeBoth(string first, string second, TaskCompletionSource mine, Task others)
        {
            try
            {
                await host.InScopeAsync(async db =>
                {
                    await using var transaction = await db.Database.BeginTransactionAsync(Ct);
                    await db.Database.TakeLockAsync(first, Ct);

                    mine.SetResult();
                    await others;

                    await db.Database.TakeLockAsync(second, Ct);
                    await transaction.CommitAsync(Ct);
                });

                return null;
            }
            catch (Exception failure)
            {
                return failure;
            }
        }

        var a = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var b = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var ended = await Task.WhenAll(TakeBoth("a", "b", a, b.Task), TakeBoth("b", "a", b, a.Task));

        var refused = Assert.IsType<TimeoutException>(Assert.Single(ended, failure => failure is not null));
        Assert.Contains("the server ended this one's wait. Roll this transaction back", refused.Message);
        Assert.Contains("in one fixed order everywhere", refused.Message);
    }

    [Fact]
    public async Task Only_an_answer_that_gives_the_lock_lets_the_caller_go_on()
    {
        await using var db = new Db(new DbContextOptionsBuilder<Db>().UseSqlite("Data Source=:memory:").Options);

        // Given at once, and given after a wait.
        Assert.Null(DatabaseLock.Refusal(db, "a", 1000, 0));
        Assert.Null(DatabaseLock.Refusal(db, "a", 1000, 1));

        // Too long, and waiting for each other: both for a retry of the whole transaction.
        Assert.IsType<TimeoutException>(DatabaseLock.Refusal(db, "a", 1000, -1));
        Assert.IsType<TimeoutException>(DatabaseLock.Refusal(db, "a", 1000, -3));

        // Cancelled, and a call the server did not take.
        Assert.IsType<InvalidOperationException>(DatabaseLock.Refusal(db, "a", 1000, -2));
        Assert.Contains("sp_getapplock answered -999", Assert.IsType<InvalidOperationException>(DatabaseLock.Refusal(db, "a", 1000, -999)).Message);
    }

    [Fact]
    public async Task Code_that_waits_for_the_lock_on_a_thread_with_a_synchronization_context_does_not_hang()
    {
        await using var host = await SavePipelineHost.StartOnSqlServerAsync("LockSync");

        await host.InScopeAsync(first => host.InScopeAsync(async second =>
        {
            await using var firsts = await first.Database.BeginTransactionAsync(Ct);
            await using var seconds = await second.Database.BeginTransactionAsync(Ct);
            await first.Database.TakeLockAsync("a", Ct);

            var got = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var thread = new Thread(() =>
            {
                // As a thread of a user interface is while it waits: nothing that is posted to it runs.
                SynchronizationContext.SetSynchronizationContext(new NobodyPumps());

                try
                {
                    second.Database.TakeLockAsync("a").GetAwaiter().GetResult();
                    got.SetResult();
                }
                catch (Exception failure)
                {
                    got.SetException(failure);
                }
            }) { IsBackground = true };

            thread.Start();
            await Task.Delay(300, Ct);
            await firsts.CommitAsync(Ct);

            await got.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        }));
    }

    private sealed class NobodyPumps : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) { }
    }

    // ── The transaction ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_rollback_to_a_savepoint_releases_the_locks_taken_after_it_and_no_other()
    {
        // As it puts back what the handlers kept for the transaction (RowSet.Items): a save after it that numbers again
        // takes the lock again, and finds the last number another transaction may have given out in between.
        await using var host = await SavePipelineHost.StartOnSqlServerAsync("LockSavepoint");

        await host.InScopeAsync(async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            await db.Database.TakeLockAsync("before", Ct);

            await transaction.CreateSavepointAsync("point", Ct);
            await db.Database.TakeLockAsync("before", Ct);
            await db.Database.TakeLockAsync("after", Ct);
            await transaction.RollbackToSavepointAsync("point", Ct);

            Assert.False(await IsFreeAsync(host, "before"));
            Assert.True(await IsFreeAsync(host, "after"));

            await db.Database.TakeLockAsync("after", Ct);
            Assert.False(await IsFreeAsync(host, "after"));

            await transaction.CommitAsync(Ct);
        });

        Assert.True(await IsFreeAsync(host, "before"));
        Assert.True(await IsFreeAsync(host, "after"));
    }

    [Fact]
    public async Task A_lock_is_taken_in_a_transaction_the_context_cannot_follow_too()
    {
        await using var host = await SavePipelineHost.StartOnSqlServerAsync("LockUnseen");

        // Joined with UseTransaction.
        await host.InScopeAsync(async db =>
        {
            await db.Database.OpenConnectionAsync(Ct);

            await using (var owner = await db.Database.GetDbConnection().BeginTransactionAsync(Ct))
            {
                await db.Database.UseTransactionAsync(owner, Ct);
                await db.Database.TakeLockAsync("joined", Ct);
                Assert.False(await IsFreeAsync(host, "joined"));

                await owner.CommitAsync(Ct);
                Assert.True(await IsFreeAsync(host, "joined"));

                // It has ended, and EF holds it still.
                var ended = await Assert.ThrowsAsync<InvalidOperationException>(() => db.Database.TakeLockAsync("joined", Ct));
                Assert.Contains("no transaction is open, or the one there was has ended", ended.Message);

                await db.Database.UseTransactionAsync(null, Ct);
            }
        });

        // Ambient.
        using (var ambient = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            await host.InScopeAsync(async db =>
            {
                await db.Database.TakeLockAsync("ambient", Ct);

                using (new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled))
                    Assert.False(await IsFreeAsync(host, "ambient"));
            });

            ambient.Complete();
        }

        Assert.True(await IsFreeAsync(host, "ambient"));

        // Enlisted.
        await host.InScopeAsync(async db =>
        {
            using var committable = new CommittableTransaction();
            await db.Database.OpenConnectionAsync(Ct);
            db.Database.EnlistTransaction(committable);

            await db.Database.TakeLockAsync("enlisted", Ct);
            Assert.False(await IsFreeAsync(host, "enlisted"));

            committable.Rollback();
        });

        Assert.True(await IsFreeAsync(host, "enlisted"));
    }

    [Fact]
    public async Task In_a_transaction_with_snapshot_isolation_it_is_refused()
    {
        // The lock would be given, and the read under it would not see what the last transaction that held it committed.
        await using var host = await SavePipelineHost.StartOnSqlServerAsync("LockSnapshot");

        await host.InScopeAsync(async db =>
        {
            await using (await db.Database.BeginTransactionAsync(IsolationLevel.Snapshot, Ct))
            {
                var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => db.Database.TakeLockAsync("a", Ct));
                Assert.Contains("in a transaction with snapshot isolation", refused.Message);
            }

            var snapshot = new TransactionOptions { IsolationLevel = System.Transactions.IsolationLevel.Snapshot };

            using (new TransactionScope(TransactionScopeOption.Required, snapshot, TransactionScopeAsyncFlowOption.Enabled))
                await Assert.ThrowsAsync<InvalidOperationException>(() => db.Database.TakeLockAsync("a", Ct));

            // Every other one will do.
            foreach (var level in new[] { IsolationLevel.ReadUncommitted, IsolationLevel.ReadCommitted, IsolationLevel.RepeatableRead, IsolationLevel.Serializable })
                await using (await db.Database.BeginTransactionAsync(level, Ct))
                    await db.Database.TakeLockAsync("a", Ct);
        });
    }

    [Fact]
    public async Task In_a_transaction_the_server_has_ended_it_is_refused()
    {
        await using var host = await SavePipelineHost.StartOnSqlServerAsync("LockEnded");

        await host.InScopeAsync(async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync("SET XACT_ABORT ON; SELECT CAST('not a number' AS int);", Ct));

            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => db.Database.TakeLockAsync("a", Ct));
            Assert.Contains("no transaction is open, or the one there was has ended", refused.Message);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_lock_needs_a_transaction(bool sqlServer)
    {
        await using var host = await SavePipelineHost.StartAsync(sqlServer, "LockWithout");

        await host.InScopeAsync(async db =>
        {
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => db.Database.TakeLockAsync("a", Ct));

            Assert.Contains("Db cannot take the lock 'a' (TakeLockAsync): no transaction is open", refused.Message);
            Assert.Contains("rows.TakeLockAsync", refused.Message);

            // After one has ended there is none either.
            await using (var transaction = await db.Database.BeginTransactionAsync(Ct))
            {
                await db.Database.TakeLockAsync("a", Ct);
                await transaction.CommitAsync(Ct);
            }

            await Assert.ThrowsAsync<InvalidOperationException>(() => db.Database.TakeLockAsync("a", Ct));
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("256")]
    public async Task A_name_that_is_empty_or_too_long_is_refused_wherever_the_lock_is_taken(string? name)
    {
        if (name == "256")
            name = new string('n', 256);

        await using var host = SavePipelineHost.Start();

        await host.InScopeAsync(async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            await Assert.ThrowsAnyAsync<ArgumentException>(() => db.Database.TakeLockAsync(name!, Ct));

            // Also on the rows of a handler's unit test, so that the mistake is found there.
            var rows = HandlerRows.Pending(db, new ChangedRow<Order>(new Order(), RowChange.Added));
            await Assert.ThrowsAnyAsync<ArgumentException>(() => rows.TakeLockAsync(name!, Ct).AsTask());
        });
    }

    // ── On SQLite ───────────────────────────────────────────────────────────────

    // A context of its own on the file, whose connection waits for another one for a second at most.
    private static Db Impatient(string connectionString) =>
        new(new DbContextOptionsBuilder<Db>().UseSqlite(new SqliteConnectionStringBuilder(connectionString) { DefaultTimeout = 1 }.ToString()).Options);

    [Fact]
    public async Task On_SQLite_the_transaction_is_the_lock_so_the_next_one_waits_where_it_begins()
    {
        await using var file = new SqliteTestFile();
        await using var first = Impatient(file.ConnectionString);
        await using var second = Impatient(file.ConnectionString);
        await first.Database.EnsureCreatedAsync(Ct);

        await using (var firsts = await first.Database.BeginTransactionAsync(Ct))
        {
            await first.Database.TakeLockAsync("a", Ct);

            // Whatever the name: it holds back whoever else would write.
            var busy = await Assert.ThrowsAsync<SqliteException>(() => second.Database.BeginTransactionAsync(Ct));
            Assert.Contains("database is locked", busy.Message);

            await firsts.CommitAsync(Ct);
        }

        await using (await second.Database.BeginTransactionAsync(Ct))
            await second.Database.TakeLockAsync("a", Ct);

        // The write that takes it changed nothing.
        Assert.Equal(0, await second.Orders.CountAsync(Ct));
    }

    public enum Reader
    {
        ReadUncommittedOnAFile,
        ReadUncommittedOnASharedCache,
        JoinedAndDeferredOnAFile,
        JoinedAndDeferredOnASharedCache,
    }

    [Theory]
    [InlineData(Reader.ReadUncommittedOnAFile)]
    [InlineData(Reader.ReadUncommittedOnASharedCache)]
    [InlineData(Reader.JoinedAndDeferredOnAFile)]
    [InlineData(Reader.JoinedAndDeferredOnASharedCache)]
    public async Task On_SQLite_a_transaction_that_began_as_a_reader_becomes_the_writer_or_is_told_that_it_cannot(Reader reader)
    {
        await using var file = new SqliteTestFile();

        var connectionString = reader is Reader.ReadUncommittedOnAFile or Reader.JoinedAndDeferredOnAFile
            ? file.ConnectionString
            : $"Data Source=lock-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";

        await using var first = Impatient(connectionString);
        await using var second = Impatient(connectionString);
        await first.Database.EnsureCreatedAsync(Ct);

        async Task<IAsyncDisposable> BeginAsync(Db db)
        {
            if (reader is Reader.ReadUncommittedOnAFile or Reader.ReadUncommittedOnASharedCache)
                return await db.Database.BeginTransactionAsync(IsolationLevel.ReadUncommitted, Ct);

            await db.Database.OpenConnectionAsync(Ct);
            var deferred = ((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred: true);
            await db.Database.UseTransactionAsync(deferred, Ct);
            return deferred;
        }

        // Both can be open at once, as two that began as writers could not.
        await using (await BeginAsync(first))
        await using (await BeginAsync(second))
        {
            await first.Database.TakeLockAsync("a", Ct);

            var busy = await Assert.ThrowsAsync<SqliteException>(() => second.Database.TakeLockAsync("a", Ct));
            Assert.Contains("locked", busy.Message);
        }
    }

    [Fact]
    public async Task On_SQLite_an_ambient_transaction_is_none_and_one_that_has_ended_is_none()
    {
        await using var host = SavePipelineHost.Start();

        await host.InScopeAsync(async db =>
        {
            // EF reports a TransactionScope as a transaction, and SQLite takes no part in it.
            using (new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
            {
                var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => db.Database.TakeLockAsync("a", Ct));
                Assert.Contains("SQLite takes no part in one", refused.Message);
            }

            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            await transaction.GetDbTransaction().CommitAsync(Ct);

            var ended = await Assert.ThrowsAsync<InvalidOperationException>(() => db.Database.TakeLockAsync("a", Ct));
            Assert.Contains("no transaction is open, or the one there was has ended", ended.Message);
        });
    }

    // ── On any other database ───────────────────────────────────────────────────

    [Theory]
    [InlineData("Npgsql.EntityFrameworkCore.PostgreSQL")]
    [InlineData("Microsoft.EntityFrameworkCore.Cosmos")]
    [InlineData(null)]
    public async Task On_a_database_the_framework_cannot_lock_on_it_says_so_instead_of_not_locking(string? provider)
    {
        await using var host = SavePipelineHost.Start();

        await host.InScopeAsync(async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);

            var refused = await Assert.ThrowsAsync<NotSupportedException>(() => DatabaseLock.TakeAsync(db, "a", provider, Ct));

            Assert.Contains($"its database provider is {provider}", refused.Message);
            Assert.Contains("on SQL Server and on SQLite only", refused.Message);
        });
    }

    [Fact]
    public async Task On_the_in_memory_provider_there_is_nothing_to_lock_with_and_the_save_goes_on()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<SaveLog>();
        services.AddDbContext<Db>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddSaveHandler<BeforeSaveTests.TicketNumbering>();
        services.AddScript(new Script<Ticket>
        {
            AfterSave = (rows, ct) => rows.TakeLockAsync("after save", ct).AsTask(),
            BeforeCommit = (rows, ct) => rows.TakeLockAsync("before commit", ct).AsTask(),
            AfterCommit = (rows, ct) => rows.TakeLockAsync("after commit", ct).AsTask(),
        });

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Db>();

        await db.Database.TakeLockAsync("outside a save", Ct);

        db.AddRange(new Ticket { Queue = "a" }, new Ticket { Queue = "a" });
        await db.SaveChangesAsync(Ct);

        Assert.Equal([1, 2], db.Tickets.Select(x => x.Number).OrderBy(x => x).ToList());
    }

    // ── In a handler ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_before_save_handler_that_takes_a_lock_gets_the_transaction_for_it(bool sqlServer)
    {
        await using var host = await SavePipelineHost.StartAsync(sqlServer, "LockBeforeSave", services => services.AddScript(new Script<Order>
        {
            BeforeSave = async (rows, ct) =>
            {
                await rows.TakeLockAsync("a", ct);
                Assert.NotNull(rows.Db.Database.CurrentTransaction);
                await rows.TakeLockAsync("b", ct);
            },
        }));

        // The save's own transaction.
        await host.InScopeAsync(async db =>
        {
            db.Add(new Order { Name = "a" });
            await db.SaveChangesAsync(Ct);
            Assert.Null(db.Database.CurrentTransaction);
        });

        Assert.Equal(["BEGIN", "LOCK", "LOCK", "INSERT Orders", "COMMIT"], host.Sql.Lines);
        host.Sql.Clear();

        // The caller's, where the first lock sets the savepoint that a failure of the save goes back to.
        await host.InScopeAsync(async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            db.Add(new Order { Name = "b" });
            await db.SaveChangesAsync(Ct);
            await transaction.CommitAsync(Ct);
        });

        Assert.Equal(["BEGIN", "SAVEPOINT", "LOCK", "LOCK", "INSERT Orders", "COMMIT"], host.Sql.Lines);

        // The synchronous save too.
        await host.InScopeAsync(db =>
        {
            db.Add(new Order { Name = "c" });
            Assert.Equal(1, db.SaveChanges());
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_handlers_after_the_write_take_a_lock_in_the_saves_transaction_and_after_the_commit_in_one_of_their_own(bool sqlServer)
    {
        var own = false;

        await using var host = await SavePipelineHost.StartAsync(sqlServer, "LockAfterWrite", services => services.AddScript(new Script<Order>
        {
            AfterSave = (rows, ct) => rows.TakeLockAsync("after save", ct).AsTask(),
            BeforeCommit = (rows, ct) => rows.TakeLockAsync("before commit", ct).AsTask(),
            AfterCommit = async (rows, ct) =>
            {
                await using var transaction = own ? await rows.Db.Database.BeginTransactionAsync(ct) : null;
                await rows.TakeLockAsync("after commit", ct);
            },
        }));

        await host.InScopeAsync(async db =>
        {
            db.Add(new Order { Name = "a" });
            var reported = await Assert.ThrowsAsync<AfterCommitException>(() => db.SaveChangesAsync(Ct));

            // The transaction that held the other two is gone. The save is committed all the same.
            var refused = Assert.IsType<InvalidOperationException>(Assert.Single(reported.Failures));
            Assert.Contains("cannot take the lock 'after commit' (TakeLockAsync): no transaction is open", refused.Message);
        });

        Assert.Equal(["BEGIN", "INSERT Orders", "LOCK", "LOCK", "COMMIT"], host.Sql.Lines);
        Assert.Equal(1, await host.InScopeAsync(db => db.Orders.CountAsync(Ct)));

        own = true;

        await host.InScopeAsync(async db =>
        {
            db.Add(new Order { Name = "b" });
            await db.SaveChangesAsync(Ct);
        });
    }

    [Fact]
    public async Task A_save_that_is_refused_a_transaction_of_its_own_names_the_lock_that_asked_for_it()
    {
        await using var host = SavePipelineHost.Start(services => services.AddScript(new Script<Order>
        {
            BeforeSave = (rows, ct) => rows.TakeLockAsync("a", ct).AsTask(),
        }));

        await host.InScopeAsync(async db =>
        {
            db.Database.AutoTransactionBehavior = AutoTransactionBehavior.Never;
            db.Add(new Order { Name = "a" });

            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
            Assert.Contains("cannot save rows that OnBeforeSave`1 asked for a transaction around (TakeLockAsync)", refused.Message);
        });

        Assert.Empty(host.Sql.Lines);
    }

    [Fact]
    public async Task On_rows_that_a_test_built_it_does_nothing_and_outside_its_handler_it_is_refused()
    {
        PendingRows<Order>? kept = null;

        await using var host = SavePipelineHost.Start(services => services.AddScript(new Script<Order>
        {
            BeforeSave = (rows, _) =>
            {
                kept = rows;
                return Task.CompletedTask;
            },
        }));

        await host.InScopeAsync(async db =>
        {
            var row = new ChangedRow<Order>(new Order { Name = "a" }, RowChange.Added);
            await HandlerRows.Pending(db, row).TakeLockAsync("a", Ct);
            await HandlerRows.Saved(db, row).TakeLockAsync("a", Ct);
            Assert.Null(db.Database.CurrentTransaction);

            db.Add(new Order { Name = "b" });
            await db.SaveChangesAsync(Ct);

            var late = await Assert.ThrowsAsync<InvalidOperationException>(() => kept!.TakeLockAsync("a", Ct).AsTask());
            Assert.Contains("TakeLockAsync asks for a transaction around the save", late.Message);
            Assert.Contains("only by that handler, while it runs", late.Message);
        });
    }
}
