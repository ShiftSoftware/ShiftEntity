using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.Tests.SaveHandlers;
using Xunit;
using static ShiftSoftware.ShiftEntity.Tests.SaveHandlers.SavePipelineModel;
using static ShiftSoftware.ShiftEntity.Tests.SaveLifecycle.ScriptedHandlers;

namespace ShiftSoftware.ShiftEntity.Tests.SaveLifecycle;

/// <summary>
/// The cost tests run alone, after the tests that run in parallel. The bytes a save allocates depend on how far the JIT
/// has optimized the code that runs it, and tests that run at the same time keep it busy: measured during the parallel
/// tests, a record cost up to 10 bytes more than measured alone.
/// </summary>
[CollectionDefinition(nameof(SaveCostCollection), DisableParallelization = true)]
public sealed class SaveCostCollection;

/// <summary>
/// What a save costs, in counts and not in time: passes of change detection, SQL commands and transactions, rows kept
/// for the stages after the write, copies of old values, and allocated bytes. And what the save must not save on: the
/// change detection of EF's own write, which finds what an app's interceptor changed.
/// </summary>
[Collection(nameof(SaveCostCollection))]
public class SaveCostTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── Handlers that do nothing ────────────────────────────────────────────────

    public sealed class NothingBeforeSave<TEntity> : IBeforeSaveHandler<TEntity> where TEntity : class
    {
        public ValueTask BeforeSaveAsync(PendingRows<TEntity> rows, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    public sealed class NothingAfterSave<TEntity> : IAfterSaveHandler<TEntity> where TEntity : class
    {
        public ValueTask AfterSaveAsync(SavedRows<TEntity> rows, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    public sealed class NothingAfterCommit<TEntity> : IAfterCommitHandler<TEntity> where TEntity : class
    {
        public ValueTask AfterCommitAsync(SavedRows<TEntity> rows, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    [NeedsOriginal]
    public sealed class NothingAfterCommitWithOriginal<TEntity> : IAfterCommitHandler<TEntity> where TEntity : class
    {
        public ValueTask AfterCommitAsync(SavedRows<TEntity> rows, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    /// <summary>Adds a memo for each new order, so that the save has a second round.</summary>
    public sealed class MemoForAnOrder : IBeforeSaveHandler<Order>
    {
        public ValueTask BeforeSaveAsync(PendingRows<Order> rows, CancellationToken cancellationToken)
        {
            foreach (var row in rows)
                if (row.Change == RowChange.Added)
                    rows.Db.Add(new Memo { Name = "for " + row.Entity.Name });

            return ValueTask.CompletedTask;
        }
    }

    private static int Passes(Db db, Action save)
    {
        var passes = 0;
        void Count(object? sender, DetectChangesEventArgs e) => passes++;

        db.ChangeTracker.DetectingAllChanges += Count;

        try
        {
            save();
        }
        finally
        {
            db.ChangeTracker.DetectingAllChanges -= Count;
        }

        return passes;
    }

    private static int PassesOfASave(SavePipelineHost host, Action<Db>? change = null)
    {
        using var scope = host.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Db>();

        (change ?? (context => context.Add(new Order { Name = "a" })))(db);

        return Passes(db, () => db.SaveChanges());
    }

    // ── Change detection ────────────────────────────────────────────────────────

    [Fact]
    public async Task A_save_makes_two_passes_of_change_detection_and_one_more_for_each_round_in_which_a_before_save_handler_ran()
    {
        // No handler at all: the context's own pass, which finds the rows to stamp, and the pass of EF's write.
        await using (var host = SavePipelineHost.Start())
            Assert.Equal(2, PassesOfASave(host));

        // Handlers for the stages after the write add no pass.
        await using (var host = SavePipelineHost.Start(services => services
            .AddSaveHandler<NothingAfterSave<Order>>()
            .AddSaveHandler<NothingAfterCommitWithOriginal<Order>>()))
            Assert.Equal(2, PassesOfASave(host));

        // A before-save handler for another type than the rows of the save runs in no round.
        await using (var host = SavePipelineHost.Start(services => services.AddSaveHandler<NothingBeforeSave<Memo>>()))
            Assert.Equal(2, PassesOfASave(host));

        // One round in which a before-save handler ran: one pass more, which finds what the handler changed.
        await using (var host = SavePipelineHost.Start(services => services.AddSaveHandler<NothingBeforeSave<Order>>()))
            Assert.Equal(3, PassesOfASave(host));

        // Two rounds: the order's handler adds a memo, and the memo's handler runs in the second.
        await using (var host = SavePipelineHost.Start(services => services
            .AddSaveHandler<MemoForAnOrder>()
            .AddSaveHandler<NothingBeforeSave<Memo>>()))
            Assert.Equal(4, PassesOfASave(host));

        // A second round with no handler in it: the memo is stamped, and nothing ran that could have changed a row.
        await using (var host = SavePipelineHost.Start(services => services.AddSaveHandler<MemoForAnOrder>()))
            Assert.Equal(3, PassesOfASave(host));
    }

    [Fact]
    public async Task A_save_with_nothing_to_write_makes_two_passes_and_a_context_without_services_saves_like_any_other()
    {
        await using var host = SavePipelineHost.Start(services => services.AddSaveHandler<NothingBeforeSave<Order>>());

        Assert.Equal(2, PassesOfASave(host, _ => { }));

        await using var bare = host.Bare();
        bare.Add(new Memo { Name = "a" });

        Assert.Equal(2, Passes(bare, () => bare.SaveChanges()));
    }

    [Fact]
    public async Task With_change_detection_switched_off_by_the_app_the_save_detects_nothing_itself()
    {
        await using var host = SavePipelineHost.Start(services => services.AddSaveHandler<NothingBeforeSave<Order>>());

        Assert.Equal(0, PassesOfASave(host, db =>
        {
            db.ChangeTracker.AutoDetectChangesEnabled = false;
            db.Add(new Order { Name = "a" });
        }));
    }

    /// <summary>An app's own interceptor. It changes every order the context tracks, whatever state the order is in.</summary>
    public sealed class StatusInterceptor : SaveChangesInterceptor
    {
        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            Change(eventData.Context!);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Change(eventData.Context!);
            return ValueTask.FromResult(result);
        }

        private static void Change(DbContext db)
        {
            foreach (var order in db.Set<Order>().Local.ToList())
                order.Status = "set by the interceptor";
        }
    }

    [Theory]
    [InlineData("no handler", true)]
    [InlineData("no handler", false)]
    [InlineData("a before-save handler", true)]
    [InlineData("a before-save handler", false)]
    [InlineData("handlers after the write", true)]
    [InlineData("handlers after the write", false)]
    public async Task What_an_apps_interceptor_and_SavingChanges_handler_change_is_written_as_in_plain_EF(string handlers, bool async)
    {
        // EF fires SavingChanges and calls the interceptors inside its own save, and then detects changes. The context
        // has detected before that, and must leave EF's pass in place: only that pass finds these changes.
        await using var host = SavePipelineHost.Start(
            services =>
            {
                if (handlers == "a before-save handler")
                    services.AddSaveHandler<NothingBeforeSave<Order>>();

                if (handlers == "handlers after the write")
                    services.AddSaveHandler<NothingAfterSave<Order>>().AddSaveHandler<NothingAfterCommitWithOriginal<Order>>();
            },
            context => context.AddInterceptors(new StatusInterceptor()));

        await host.InScopeAsync(async db =>
        {
            db.AddRange(new Order { Name = "unchanged", Status = "new" }, new Order { Name = "modified", Status = "new" });

            using (db.SuppressAllSaveHandlers())
                await db.SaveChangesAsync(Ct);
        });

        await using (var seeded = host.Bare())
            await seeded.Orders.ExecuteUpdateAsync(set => set.SetProperty(x => x.Status, "new"), Ct);

        await host.InScopeAsync(async db =>
        {
            var orders = await db.Orders.OrderBy(x => x.ID).ToListAsync(Ct);

            orders[1].Address.City = "moved";
            db.Add(new Order { Name = "added", Status = "new" });

            db.SavingChanges += (sender, _) =>
            {
                foreach (var order in ((Db)sender!).Orders.Local.ToList())
                    order.Name += ", renamed by the event";
            };

            if (async)
                await db.SaveChangesAsync(Ct);
            else
                db.SaveChanges();
        });

        await using var bare = host.Bare();
        var stored = await bare.Orders.AsNoTracking().OrderBy(x => x.ID).ToListAsync(Ct);

        Assert.Equal(
        [
            "unchanged, renamed by the event: set by the interceptor",
            "modified, renamed by the event: set by the interceptor",
            "added, renamed by the event: set by the interceptor",
        ], stored.Select(order => $"{order.Name}: {order.Status}"));

        Assert.Equal("moved", stored[1].Address.City);
    }

    // ── SQL commands, transactions and savepoints ───────────────────────────────

    private static async Task<List<string>> SqlOfASaveAsync(SavePipelineHost host, int rows, bool inATransaction = false)
    {
        await host.InScopeAsync(async db =>
        {
            await using var transaction = inATransaction ? await db.Database.BeginTransactionAsync(Ct) : null;

            db.AddRange(Enumerable.Range(0, rows).Select(i => new Order { Name = "order " + i }));
            await db.SaveChangesAsync(Ct);

            if (transaction is not null)
                await transaction.CommitAsync(Ct);
        });

        return host.Sql.Lines;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Handlers_for_other_types_and_handlers_that_need_no_transaction_add_no_SQL_to_a_save(int rows)
    {
        List<string> plain;

        await using (var host = SavePipelineHost.Start())
            plain = await SqlOfASaveAsync(host, rows);

        // EF sends one row as one command. It gives a save of several commands a transaction of its own.
        Assert.Equal(rows == 1 ? ["INSERT Orders"] : ["BEGIN", "INSERT Orders", "INSERT Orders", "INSERT Orders", "COMMIT"], plain);

        // A host full of handlers, none of them for an order.
        await using (var host = SavePipelineHost.Start(services => services
            .AddSaveHandler<NothingBeforeSave<Memo>>()
            .AddSaveHandler<NothingAfterSave<Memo>>()
            .AddSaveHandler<NothingAfterCommitWithOriginal<Ticket>>()))
            Assert.Equal(plain, await SqlOfASaveAsync(host, rows));

        // Before-save and after-commit handlers of the order itself: still no transaction of the context's, and no
        // command more.
        await using (var host = SavePipelineHost.Start(services => services
            .AddSaveHandler<NothingBeforeSave<Order>>()
            .AddSaveHandler<NothingAfterCommitWithOriginal<Order>>()))
            Assert.Equal(plain, await SqlOfASaveAsync(host, rows));
    }

    [Fact]
    public async Task An_after_save_handler_costs_a_one_row_save_a_begin_and_a_commit_and_a_larger_save_nothing()
    {
        // One row: EF needs no transaction for it, so the context's is two round trips more. No savepoint.
        await using (var host = SavePipelineHost.Start(services => services.AddSaveHandler<NothingAfterSave<Order>>()))
            Assert.Equal(["BEGIN", "INSERT Orders", "COMMIT"], await SqlOfASaveAsync(host, rows: 1));

        // Several rows: the context's transaction takes the place of the one EF would open itself.
        await using (var host = SavePipelineHost.Start(services => services.AddSaveHandler<NothingAfterSave<Order>>()))
            Assert.Equal(["BEGIN", "INSERT Orders", "INSERT Orders", "INSERT Orders", "COMMIT"], await SqlOfASaveAsync(host, rows: 3));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Inside_a_transaction_of_the_apps_a_save_with_after_save_work_sends_what_a_save_without_it_sends(bool sqlServer)
    {
        // EF makes a savepoint for a save inside a transaction of the app's. The context makes its own in its place,
        // and not a second one.
        List<string> plain;

        await using (var host = await SavePipelineHost.StartAsync(sqlServer, "CostInATransaction"))
            plain = await SqlOfASaveAsync(host, rows: 3, inATransaction: true);

        Assert.Single(plain, "SAVEPOINT");

        await using (var host = await SavePipelineHost.StartAsync(sqlServer, "CostInATransaction", services => services
            .AddSaveHandler<NothingAfterSave<Order>>()
            .AddSaveHandler<NothingAfterCommit<Order>>()))
            Assert.Equal(plain, await SqlOfASaveAsync(host, rows: 3, inATransaction: true));
    }

    // ── Rows kept, and old values copied ────────────────────────────────────────

    private static async Task<(long Collected, long Copied)> KeptByASaveAsync(SavePipelineHost host, int rows, bool modify)
    {
        await host.InScopeAsync(async db =>
        {
            db.AddRange(Enumerable.Range(0, rows).Select(i => new Order { Name = "order " + i }));

            using (db.SuppressAllSaveHandlers())
                await db.SaveChangesAsync(Ct);
        });

        return await host.InScopeAsync(async db =>
        {
            if (modify)
            {
                foreach (var order in await db.Orders.ToListAsync(Ct))
                    order.Status = "changed";
            }
            else
            {
                db.AddRange(Enumerable.Range(0, rows).Select(i => new Order { Name = "more " + i }));
            }

            await db.SaveChangesAsync(Ct);

            return (db.RowsCollected, db.OriginalsCopied);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_save_keeps_rows_only_for_the_types_someone_asked_for_and_copies_old_values_only_for_handlers_that_need_them(bool modify)
    {
        const int rows = 40;

        // Nothing registered: nothing is kept.
        await using (var host = SavePipelineHost.Start())
            Assert.Equal((0L, 0L), await KeptByASaveAsync(host, rows, modify));

        // Handlers for other types, one of them with old values: still nothing for the orders.
        await using (var host = SavePipelineHost.Start(services => services
            .AddSaveHandler<NothingAfterSave<Memo>>()
            .AddSaveHandler<NothingAfterCommitWithOriginal<Ticket>>()
            .AddSaveHandler<NothingBeforeSave<Memo>>()))
            Assert.Equal((0L, 0L), await KeptByASaveAsync(host, rows, modify));

        // A before-save handler alone needs nothing after the write.
        await using (var host = SavePipelineHost.Start(services => services.AddSaveHandler<NothingBeforeSave<Order>>()))
            Assert.Equal((0L, 0L), await KeptByASaveAsync(host, rows, modify));

        // An after-commit handler: one row each, and no copy.
        await using (var host = SavePipelineHost.Start(services => services.AddSaveHandler<NothingAfterCommit<Order>>()))
            Assert.Equal(((long)rows, 0L), await KeptByASaveAsync(host, rows, modify));

        // With old values: one copy for each modified row, and none for a new row, which has no old values.
        await using (var host = SavePipelineHost.Start(services => services.AddSaveHandler<NothingAfterCommitWithOriginal<Order>>()))
            Assert.Equal(((long)rows, modify ? (long)rows : 0L), await KeptByASaveAsync(host, rows, modify));
    }

    // ── The frame of a save ─────────────────────────────────────────────────────

    [Fact]
    public async Task A_save_that_has_ended_leaves_nothing_in_the_frame_the_next_save_takes_over()
    {
        // A save on its own reuses the frame of the context's last one. Whatever a save left in it would be read by
        // the next save as its own: a transaction, a repository, a switch. So every field must be as new, after a
        // save that used them all, and after one that failed.
        var spareFrame = typeof(ShiftDbContext).GetField("spareFrame", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        void AssertAsNew(Db db)
        {
            var frame = spareFrame.GetValue(db);
            Assert.NotNull(frame);

            foreach (var field in frame.GetType().GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
            {
                var value = field.GetValue(frame);
                var asNew = field.FieldType.IsValueType ? Activator.CreateInstance(field.FieldType) : null;

                Assert.True(Equals(asNew, value), $"The frame's {field.Name} was left as {value}.");
            }
        }

        await using var host = SavePipelineHost.Start(services => services
            .AddSaveHandler<TicketNumberingProbe>()
            .AddSaveHandler<NothingAfterSave<Ticket>>()
            .AddSaveHandler<NothingAfterCommitWithOriginal<Ticket>>()
            .AddScript(new Script<Memo> { AfterSave = (rows, _) => rows[0].Entity.Name == "fails" ? throw new InvalidOperationException("no") : Task.CompletedTask }));

        await host.InScopeAsync(async db =>
        {
            // A save that asks for a transaction before the write, under a switch, with handlers in every stage.
            db.Add(new Ticket { Queue = "a", Name = "first" });

            using (db.SuppressSaveHandlers(SaveStages.BeforeCommit))
                await db.SaveChangesAsync(Ct);

            AssertAsNew(db);

            // The same frame serves the next save, and the one after.
            var frame = spareFrame.GetValue(db);

            db.Add(new Ticket { Queue = "a", Name = "second" });
            db.SaveChanges();

            Assert.Same(frame, spareFrame.GetValue(db));
            AssertAsNew(db);

            // Inside a transaction of the caller's, with a savepoint.
            await using (var transaction = await db.Database.BeginTransactionAsync(Ct))
            {
                db.Add(new Ticket { Queue = "b", Name = "third" });
                await db.SaveChangesAsync(Ct);
                AssertAsNew(db);

                await transaction.CommitAsync(Ct);
            }

            AssertAsNew(db);

            // A save that fails after its write, and is undone.
            db.Add(new Memo { Name = "fails" });
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));

            AssertAsNew(db);
            db.ChangeTracker.Clear();

            // And a save with nothing to write.
            await db.SaveChangesAsync(Ct);
            AssertAsNew(db);
        });

        Assert.Equal([1, 2, 1], await host.InScopeAsync(db => db.Tickets.OrderBy(x => x.ID).Select(x => x.Number).ToListAsync(Ct)));
    }

    /// <summary>Numbers a ticket inside a transaction it asks for, so that a save has one of its own before the write.</summary>
    public sealed class TicketNumberingProbe : IBeforeSaveHandler<Ticket>
    {
        public async ValueTask BeforeSaveAsync(PendingRows<Ticket> rows, CancellationToken cancellationToken)
        {
            await rows.RequireTransactionAsync(cancellationToken);

            foreach (var row in rows)
                if (row.Change == RowChange.Added)
                    row.Entity.Number = await rows.Db.Set<Ticket>().CountAsync(ticket => ticket.Queue == row.Entity.Queue, cancellationToken) + 1;
        }
    }

    // ── Exceptions ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_save_throws_and_catches_nothing_in_a_host_without_a_claim_provider()
    {
        // The stamp of a new row asks for the claims of the current user. A host without a claim provider (a tool, a
        // function, a test) has none to give. That must cost a lookup, and not an exception that is thrown and caught
        // in every save. The flag marks this test's own flow, because other tests throw at the same time.
        var mine = new AsyncLocal<bool>();
        var thrown = new System.Collections.Concurrent.ConcurrentQueue<string>();

        void Note(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
        {
            if (mine.Value)
                thrown.Enqueue($"{e.Exception.GetType().Name}: {e.Exception.Message}");
        }

        await using var host = SavePipelineHost.Start(services => services.AddSaveHandler<NothingAfterCommit<Order>>());

        await host.InScopeAsync(async db =>
        {
            await using var bare = host.Bare();

            // A first save of each context, so that nothing of a context's first use is counted.
            db.Add(new Order { Name = "first" });
            await db.SaveChangesAsync(Ct);
            bare.Add(new Memo { Name = "first" });
            await bare.SaveChangesAsync(Ct);

            AppDomain.CurrentDomain.FirstChanceException += Note;

            try
            {
                mine.Value = true;

                // New rows, a modified row and nothing at all, from a scope's context.
                var order = new Order { Name = "new" };
                db.Add(order);
                db.Add(new Memo { Name = "new" });
                await db.SaveChangesAsync(Ct);

                order.Name = "changed";
                await db.SaveChangesAsync(Ct);
                await db.SaveChangesAsync(Ct);

                db.Add(new Memo { Name = "not async" });
                db.SaveChanges();

                // And a new row from a context that has no services.
                bare.Add(new Memo { Name = "new" });
                await bare.SaveChangesAsync(Ct);
            }
            finally
            {
                mine.Value = false;
                AppDomain.CurrentDomain.FirstChanceException -= Note;
            }
        });

        Assert.Empty(thrown);
    }

    // ── Allocations ─────────────────────────────────────────────────────────────

    // The bytes one synchronous save of new records allocates on this thread.
    private static long BytesOfASave(SavePipelineHost host, int rows, Func<object>? record = null) =>
        BytesOfASave<Db>(host.Provider, rows, record ?? (() => new Order { Name = "order" }));

    private static long BytesOfASave<TDb>(IServiceProvider provider, int rows, Func<object> record) where TDb : DbContext
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TDb>();

        for (var i = 0; i < rows; i++)
            db.Add(record());

        var before = GC.GetAllocatedBytesForCurrentThread();
        db.SaveChanges();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    /// <summary>
    /// The smallest number of bytes each measure allocates, once the figures have stopped falling, and how many turns
    /// that took.
    /// </summary>
    /// <remarks>
    /// Code that has run more often allocates less: the runtime first runs a method as it compiled it quickly, and later
    /// swaps in code that it optimized on a background thread, which keeps some objects off the heap. A figure taken
    /// before that swap and one taken after do not compare, and the swap comes later on a busy machine. So every measure
    /// first runs a few times uncounted. Then the measures take turns, with a short pause after each turn in which the
    /// background compiler can finish, until a few turns in a row have lowered none of the figures by more than a few
    /// hundred bytes: a save of the same records varies by about that much, a fraction of a byte a record.
    /// </remarks>
    private static (Dictionary<string, long> Least, int Turns) LeastBytes(params (string Name, Func<long> Measure)[] measures)
    {
        const int uncounted = 3, fewestTurns = 5, quietTurns = 3, mostTurns = 40;
        const long noise = 256;
        var pause = TimeSpan.FromMilliseconds(200);

        for (var turn = 0; turn < uncounted; turn++)
        {
            foreach (var (_, measure) in measures)
                measure();

            Thread.Sleep(pause);
        }

        var least = measures.ToDictionary(measure => measure.Name, _ => long.MaxValue);
        var turns = 0;

        for (var quiet = 0; turns < mostTurns && (turns < fewestTurns || quiet < quietTurns);)
        {
            var lowered = false;

            foreach (var (name, measure) in measures)
            {
                var bytes = measure();

                if (bytes < least[name])
                {
                    lowered |= least[name] != long.MaxValue && least[name] - bytes > noise;
                    least[name] = bytes;
                }
            }

            turns++;
            quiet = lowered ? 0 : quiet + 1;
            Thread.Sleep(pause);
        }

        return (least, turns);
    }

    /// <summary>A record with two owned parts, for budget C: a parcel, and the two addresses it travels between.</summary>
    public static class ParcelModel
    {
        public class Parcel : ShiftSoftware.ShiftEntity.Core.ShiftEntity<Parcel>
        {
            public string? Name { get; set; }
            public Address From { get; set; } = new();
            public Address To { get; set; } = new();
        }

        public class Db(DbContextOptions<Db> options) : ShiftDbContext(options)
        {
            public DbSet<Parcel> Parcels => Set<Parcel>();

            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Parcel>().OwnsOne(x => x.From);
                modelBuilder.Entity<Parcel>().OwnsOne(x => x.To);
                base.OnModelCreating(modelBuilder);
            }
        }

        /// <summary>A host of the parcel model on one in-memory SQLite connection, with the handlers a test adds.</summary>
        public sealed class Host : IAsyncDisposable
        {
            private readonly Microsoft.Data.Sqlite.SqliteConnection connection = new("Data Source=:memory:");

            public Host(Action<IServiceCollection>? services = null)
            {
                connection.Open();

                var collection = new ServiceCollection();
                collection.AddDbContext<Db>(options => options.UseSqlite(connection));
                services?.Invoke(collection);
                Provider = collection.BuildServiceProvider(validateScopes: true);

                using var scope = Provider.CreateScope();
                scope.ServiceProvider.GetRequiredService<Db>().Database.EnsureCreated();
            }

            public ServiceProvider Provider { get; }

            public async ValueTask DisposeAsync()
            {
                await Provider.DisposeAsync();
                await connection.DisposeAsync();
            }
        }
    }

    // What EF allocates to report one entry that it accepts to a listener that reads which entry it is: the event's
    // arguments and the entry. Whoever listens pays it, so the budget counts the framework's own bytes on top.
    private const int ReportedByEF = 72;

    [Fact]
    public async Task Rows_of_a_type_with_nothing_registered_cost_about_the_same_in_a_host_that_has_handlers_for_other_types()
    {
        const int rows = 400;

        await using var nothing = SavePipelineHost.Start();

        await using var others = SavePipelineHost.Start(services => services
            .AddSaveHandler<NothingBeforeSave<Memo>>()
            .AddSaveHandler<NothingAfterSave<Memo>>()
            .AddSaveHandler<NothingAfterCommitWithOriginal<Ticket>>());

        // The hosts take turns, and the smallest figure of each counts: code that has run more often allocates less,
        // so figures taken one host after the other would not compare.
        long least = long.MaxValue, leastWithOthers = long.MaxValue;

        for (var turn = 0; turn < 8; turn++)
        {
            least = Math.Min(least, BytesOfASave(nothing, rows));
            leastWithOthers = Math.Min(leastWithOthers, BytesOfASave(others, rows));
        }

        var perOrder = (double)least / rows;
        var forOthers = (double)(leastWithOthers - least) / rows;

        // A record of a type with nothing registered, in a host that has handlers for other types: at most 1% more.
        // In such a host the context listens to EF's state changes, and that is all it does for these records: about
        // 147 bytes an order here, EF's 72 for each of its two entries (its own and its owned address), where saving an
        // order allocates about 26,000.
        Assert.True(forOthers <= perOrder * 0.01,
            $"A new order costs {perOrder:N0} bytes with nothing registered, and {forOthers:N0} bytes more in a host with handlers for other types. " +
            "That is more than 1%.");
    }

    [Fact]
    public async Task A_record_with_an_after_commit_handler_costs_at_most_100_bytes_of_the_frameworks_own_and_32_more_for_each_owned_part_after_the_first()
    {
        // Budget C counts records. A record is one row: an owned part is part of its owner's record, and EF tracks it as
        // an entry of its own. A memo has no owned part, an order one (its address), a parcel two (the addresses it
        // travels between).
        const int fewer = 400, more = 1200;

        await using var nothing = SavePipelineHost.Start();
        await using var orders = SavePipelineHost.Start(services => services.AddSaveHandler<NothingAfterCommit<Order>>());
        await using var memos = SavePipelineHost.Start(services => services.AddSaveHandler<NothingAfterCommit<Memo>>());
        await using var noParcelHandler = new ParcelModel.Host();
        await using var parcels = new ParcelModel.Host(services => services.AddSaveHandler<NothingAfterCommit<ParcelModel.Parcel>>());

        var records = new (string Name, int OwnedParts, IServiceProvider Without, IServiceProvider With)[]
        {
            ("memo", 0, nothing.Provider, memos.Provider),
            ("order", 1, nothing.Provider, orders.Provider),
            ("parcel", 2, noParcelHandler.Provider, parcels.Provider),
        };

        long Save(string record, IServiceProvider provider, int rows) => record switch
        {
            "memo" => BytesOfASave<Db>(provider, rows, () => new Memo { Name = "memo" }),
            "order" => BytesOfASave<Db>(provider, rows, () => new Order { Name = "order" }),
            _ => BytesOfASave<ParcelModel.Db>(provider, rows, () => new ParcelModel.Parcel { Name = "parcel" }),
        };

        var (least, turns) = LeastBytes(records
            .SelectMany(record => new[] { fewer, more }.SelectMany(rows => new (string, Func<long>)[]
            {
                ($"{record.Name} {rows}", () => Save(record.Name, record.Without, rows)),
                ($"{record.Name} with the handler {rows}", () => Save(record.Name, record.With, rows)),
            }))
            .ToArray());

        // What each further record costs: the difference between a save of more records and one of fewer, divided by the
        // records between them. What a save allocates once, whatever its size, is then not counted as the records': a
        // few kilobytes with the handler in this build, which allocates the state of every asynchronous method. The
        // budget's own measure is a save of 5,000 records, where that part is under a byte a record.
        double PerRecord(string name) => (double)(least[$"{name} {more}"] - least[$"{name} {fewer}"]) / (more - fewer);

        var failures = new List<string>();

        foreach (var (name, ownedParts, _, _) in records)
        {
            var entries = 1 + ownedParts;
            var own = PerRecord($"{name} with the handler") - PerRecord(name) - entries * ReportedByEF;

            // The framework's own bytes: the look before the write takes one entry of EF's for each entry of the record
            // (32 bytes each), and the context keeps the record's row for the commit (32). About 64 a memo, 96 an order
            // and 128 a parcel. The look skips an owned entry only when nothing after the write needs it, which is not so
            // while the record's type has a handler after the write.
            var budget = 100 + 32 * Math.Max(0, ownedParts - 1);

            TestContext.Current.TestOutputHelper?.WriteLine(
                $"A new {name} with {ownedParts} owned part(s): {own:N1} bytes of the framework's own, budget {budget}.");

            if (own > budget)
                failures.Add($"A new {name} with {ownedParts} owned part(s) costs {own:N1} bytes of the framework's own with an after-commit handler, " +
                    $"on top of the {ReportedByEF} that EF allocates to report each of its {entries} entries. The budget is {budget}: 100 a record, " +
                    "and 32 more for each owned part after the first.");
        }

        TestContext.Current.TestOutputHelper?.WriteLine($"Measured in {turns} counted turns: " +
            string.Join(", ", least.Select(figure => $"{figure.Key} {figure.Value:N0}")));

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }
}
