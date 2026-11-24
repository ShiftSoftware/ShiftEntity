using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.EFCore;
using Xunit;
using static ShiftSoftware.ShiftEntity.Tests.SaveHandlers.SavePipelineModel;

namespace ShiftSoftware.ShiftEntity.Tests.SaveHandlers;

/// <summary>
/// Save handlers: the host's own code that takes part in the saves of a <see cref="ShiftDbContext"/>. These pin which
/// rows a handler gets, in which order handlers run, and what a row tells a handler. The stages and their transactions
/// are pinned in the save lifecycle tests.
/// </summary>
public class SaveHandlerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── Which rows ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_handler_is_called_once_for_a_save_with_the_rows_of_its_type_that_the_save_added_modified_or_deleted()
    {
        await using var host = SavePipelineHost.Start(services => services.AddSaveHandler<CommitLog<Order>>());

        await host.InScopeAsync(async db =>
        {
            var first = new Order { Name = "first" };
            var second = new Order { Name = "second" };
            db.AddRange(first, second, new Memo { Name = "not an order" });
            await db.SaveChangesAsync(Ct);

            first.Name = "renamed";
            db.Remove(second);
            await db.SaveChangesAsync(Ct);

            await db.SaveChangesAsync(Ct);      // a save that writes nothing calls nobody
        });

        Assert.Equal(
        [
            "after commit of Order: Added Order 'first', Added Order 'second'",
            "after commit of Order: Modified Order 'renamed', Deleted Order 'second'",
        ], host.Log.Lines);
    }

    [Fact]
    public async Task A_handler_for_a_base_class_or_an_interface_gets_every_row_that_is_one()
    {
        await using var host = SavePipelineHost.Start(services => services
            .AddSaveHandler<CommitLog<Order>>()
            .AddSaveHandler<CommitLog<INamed>>());

        await host.InScopeAsync(async db =>
        {
            db.AddRange(new RushOrder { Name = "rush" }, new Memo { Name = "memo" });
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(
        [
            "after commit of Order: Added RushOrder 'rush'",
            "after commit of INamed: Added RushOrder 'rush', Added Memo 'memo'",
        ], host.Log.Lines);
    }

    public sealed class First(SaveLog log) : IAfterCommitHandler<Order>
    {
        public ValueTask AfterCommitAsync(SavedRows<Order> rows, CancellationToken cancellationToken)
        {
            log.Add("first");
            return ValueTask.CompletedTask;
        }
    }

    public sealed class Last(SaveLog log) : IAfterCommitHandler<Order>
    {
        public ValueTask AfterCommitAsync(SavedRows<Order> rows, CancellationToken cancellationToken)
        {
            log.Add("last");
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task Handlers_of_a_stage_run_by_their_order_then_as_they_were_added_and_the_same_class_twice_is_one_handler()
    {
        await using var host = SavePipelineHost.Start(services => services
            .AddSaveHandler<Last>(order: 10)
            .AddSaveHandler<CommitLog<INamed>>()
            .AddSaveHandler<CommitLog<Order>>()
            .AddSaveHandler<First>(order: -1)
            .AddSaveHandler<CommitLog<INamed>>(order: 99));     // the first declaration counts

        await host.InScopeAsync(async db =>
        {
            db.Add(new Order { Name = "one" });
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(
            ["first", "after commit of INamed: Added Order 'one'", "after commit of Order: Added Order 'one'", "last"],
            host.Log.Lines);
    }

    public sealed class NotAHandler;

    public abstract class AbstractHandler : IAfterCommitHandler<Order>
    {
        public abstract ValueTask AfterCommitAsync(SavedRows<Order> rows, CancellationToken cancellationToken);
    }

    [Fact]
    public void A_class_that_implements_no_stage_or_cannot_be_created_is_refused_where_it_is_added()
    {
        var services = new ServiceCollection();

        var none = Assert.Throws<ArgumentException>(() => services.AddSaveHandler<NotAHandler>());
        Assert.Contains("NotAHandler", none.Message);
        Assert.Contains("IAfterCommitHandler<T>", none.Message);

        Assert.Throws<ArgumentException>(() => services.AddSaveHandler<AbstractHandler>());
        Assert.Empty(services);
    }

    public sealed class OrdersAndMemos(SaveLog log) : IAfterCommitHandler<Order>, IAfterCommitHandler<Memo>, IAfterSaveHandler<Memo>
    {
        public ValueTask AfterCommitAsync(SavedRows<Order> rows, CancellationToken cancellationToken)
        {
            log.Add($"orders after commit: {rows.Count}");
            return ValueTask.CompletedTask;
        }

        public ValueTask AfterCommitAsync(SavedRows<Memo> rows, CancellationToken cancellationToken)
        {
            log.Add($"memos after commit: {rows.Count}");
            return ValueTask.CompletedTask;
        }

        public ValueTask AfterSaveAsync(SavedRows<Memo> rows, CancellationToken cancellationToken)
        {
            log.Add($"memos after save: {rows.Count}");
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task One_class_takes_part_in_every_stage_and_entity_type_it_implements()
    {
        await using var host = SavePipelineHost.Start(services => services.AddSaveHandler<OrdersAndMemos>());

        await host.InScopeAsync(async db =>
        {
            db.AddRange(new Order { Name = "a" }, new Memo { Name = "b" }, new Memo { Name = "c" });
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(["memos after save: 2", "memos after commit: 2", "orders after commit: 1"], host.Log.Lines);
    }

    // ── When ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Handlers_run_after_every_observer_whatever_the_order_of_registration()
    {
        await using var host = SavePipelineHost.Start(services => services
            .AddSaveHandler<StageLog<Memo>>()
            .AddScoped<IShiftDbContextSaveObserver, LoggingSaveObserver>());

        await host.InScopeAsync(async db =>
        {
            db.Add(new Memo { Name = "one" });
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(
        [
            "observer: Added Memo",
            "after save of Memo: Added Memo 'one'",
            "before commit of Memo: Added Memo 'one'",
            "after commit of Memo: Added Memo 'one'",
        ], host.Log.Lines);
    }

    public sealed class CountedHandler : IAfterCommitHandler<Order>
    {
        public static int Built;

        public CountedHandler() => Interlocked.Increment(ref Built);

        public ValueTask AfterCommitAsync(SavedRows<Order> rows, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task A_handler_is_created_only_when_a_row_of_its_type_was_saved()
    {
        await using var host = SavePipelineHost.Start(services => services.AddSaveHandler<CountedHandler>());
        CountedHandler.Built = 0;

        await host.InScopeAsync(async db =>
        {
            db.Add(new Memo { Name = "memo" });
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(0, CountedHandler.Built);

        await host.InScopeAsync(async db =>
        {
            db.AddRange(new Order { Name = "one" }, new Order { Name = "two" });
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(1, CountedHandler.Built);
    }

    public sealed class ChecksItsContext(Db injected, SaveLog log) : IAfterSaveHandler<Order>, IAfterCommitHandler<Order>
    {
        public ValueTask AfterSaveAsync(SavedRows<Order> rows, CancellationToken cancellationToken)
        {
            log.Add($"same context: {ReferenceEquals(rows.Db, injected)}, bag empty: {rows.Items.Count == 0}");
            rows.Items["note"] = "left by after save";
            return ValueTask.CompletedTask;
        }

        public ValueTask AfterCommitAsync(SavedRows<Order> rows, CancellationToken cancellationToken)
        {
            log.Add($"after commit found: {rows.Items["note"]}");
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task A_handler_is_given_the_saving_context_and_a_bag_that_the_stages_of_one_commit_share()
    {
        await using var host = SavePipelineHost.Start(services => services.AddSaveHandler<ChecksItsContext>());

        await host.InScopeAsync(async db =>
        {
            db.Add(new Order { Name = "one" });
            await db.SaveChangesAsync(Ct);

            // The next save commits on its own, so it starts with an empty bag.
            db.Add(new Order { Name = "two" });
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(
        [
            "same context: True, bag empty: True", "after commit found: left by after save",
            "same context: True, bag empty: True", "after commit found: left by after save",
        ], host.Log.Lines);
    }

    // ── Values from before the save ─────────────────────────────────────────────

    [Fact]
    public async Task A_handler_marked_NeedsOriginal_gets_what_a_modified_and_a_deleted_row_held_before_the_save()
    {
        await using var host = SavePipelineHost.Start(services => services.AddSaveHandler<OrderBeforeAndAfter>());

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

            // Changed in memory and then removed: the copy is what the database held.
            order.Name = "never written";
            db.Remove(order);
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(
        [
            "Added: ''/'' -> 'draft'/'New'",
            "Modified: 'draft'/'New' -> 'draft'/'Resolved'",
            "Deleted: 'draft'/'Resolved' -> 'never written'/'Resolved'",
        ], host.Log.Lines);
    }

    public sealed class ReadsOriginalWithoutAsking(SaveLog log) : IAfterCommitHandler<Order>
    {
        public ValueTask AfterCommitAsync(SavedRows<Order> rows, CancellationToken cancellationToken)
        {
            foreach (var row in rows)
            {
                try
                {
                    log.Add($"original: '{row.Original?.Name}'");
                }
                catch (InvalidOperationException exception)
                {
                    log.Add("refused: " + exception.Message);
                }
            }

            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task A_handler_that_is_not_marked_is_told_so_instead_of_reading_nothing()
    {
        await using var host = SavePipelineHost.Start(services => services.AddSaveHandler<ReadsOriginalWithoutAsking>());

        await host.InScopeAsync(async db =>
        {
            var order = new Order { Name = "draft" };
            db.Add(order);
            await db.SaveChangesAsync(Ct);

            order.Name = "renamed";
            await db.SaveChangesAsync(Ct);
        });

        // An added row had no values before the save: null, for every handler. A modified row's values are withheld from
        // a handler that is not marked, and it is told so.
        Assert.Equal(2, host.Log.Lines.Count);
        Assert.Equal("original: ''", host.Log.Lines[0]);
        Assert.StartsWith("refused: ReadsOriginalWithoutAsking reads ChangedRow<Order>.Original after the write", host.Log.Lines[1]);
        Assert.Contains("[NeedsOriginal]", host.Log.Lines[1]);
    }

    /// <summary>Tells new rows from changed ones the way the doc comment allows: an added row's Original is null.</summary>
    public sealed class TellsAddedFromModified(SaveLog log) : IAfterCommitHandler<Memo>
    {
        public ValueTask AfterCommitAsync(SavedRows<Memo> rows, CancellationToken cancellationToken)
        {
            foreach (var row in rows)
                log.Add(row.Original is null ? $"added '{row.Entity.Name}'" : $"modified '{row.Entity.Name}'");

            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task Original_of_an_added_row_is_null_for_a_handler_without_the_mark_and_a_modified_row_still_needs_it()
    {
        await using var host = SavePipelineHost.Start(services => services.AddSaveHandler<TellsAddedFromModified>());

        await host.InScopeAsync(async db =>
        {
            // A save that only adds a row: nothing to withhold, so the handler works without the mark.
            var memo = new Memo { Name = "new" };
            db.Add(memo);
            await db.SaveChangesAsync(Ct);

            // A modified row: the values from before are withheld from a handler that is not marked.
            memo.Name = "renamed";
            var failed = await Assert.ThrowsAsync<AfterCommitException>(() => db.SaveChangesAsync(Ct));
            Assert.Contains("Mark the class with [NeedsOriginal]", Assert.Single(failed.Failures).Message);
        });

        Assert.Equal(["added 'new'"], host.Log.Lines);
    }

    /// <summary>Reads Original after the write, and is not marked.</summary>
    public sealed class ReadsOriginalWithoutTheMark(SaveLog log) : IAfterCommitHandler<Memo>
    {
        public ValueTask AfterCommitAsync(SavedRows<Memo> rows, CancellationToken cancellationToken)
        {
            foreach (var row in rows)
                log.Add($"{row.Change} was '{row.Original?.Name}'");

            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task A_handler_test_built_for_the_handler_class_fails_like_the_real_save_when_the_mark_is_missing()
    {
        await using var host = SavePipelineHost.Start(services => services.AddSaveHandler<ReadsOriginalWithoutTheMark>());
        var log = host.Log;

        await using (var seed = host.Bare())
        {
            seed.Memos.Add(new() { Name = "old" });
            await seed.SaveChangesAsync(Ct);
        }

        // The real save: the handler reads Original of a modified row without the mark.
        var real = await host.InScopeAsync(async db =>
        {
            var memo = await db.Memos.SingleAsync(Ct);
            memo.Name = "new";
            return Assert.Single((await Assert.ThrowsAsync<AfterCommitException>(() => db.SaveChangesAsync(Ct))).Failures);
        });

        await host.InScopeAsync(async db =>
        {
            ChangedRow<Memo> Modified() => new(new Memo { Name = "new" }, RowChange.Modified, new Memo { Name = "old" });

            // Built for the handler class, the unit test fails in the same way.
            var rows = HandlerRows.Saved<ReadsOriginalWithoutTheMark, Memo>(db, Modified());
            var inTest = await Assert.ThrowsAsync<InvalidOperationException>(() => new ReadsOriginalWithoutTheMark(log).AfterCommitAsync(rows, Ct).AsTask());
            Assert.Equal(real.Message, inTest.Message);
            Assert.IsType<InvalidOperationException>(real);

            // An added row gives null either way. A deleted row's values are withheld, as a modified row's are.
            var added = HandlerRows.Saved<ReadsOriginalWithoutTheMark, Memo>(db, new ChangedRow<Memo>(new Memo { Name = "added" }, RowChange.Added));
            Assert.Null(added[0].Original);
            Assert.Throws<InvalidOperationException>(() => HandlerRows.Saved<ReadsOriginalWithoutTheMark, Memo>(db,
                new ChangedRow<Memo>(new Memo { Name = "gone" }, RowChange.Deleted, new Memo { Name = "gone" }))[0].Original);

            // The set that does not name the class gives every row's Original, as before.
            Assert.Equal("old", HandlerRows.Saved(db, Modified())[0].Original?.Name);

            // A class that is marked gets them.
            var marked = HandlerRows.Saved<OrderBeforeAndAfter, Order>(db,
                new ChangedRow<Order>(new Order { Name = "after" }, RowChange.Modified, new Order { Name = "before" }));
            Assert.Equal("before", marked[0].Original?.Name);

            // A class with no stage after the write is refused.
            Assert.Throws<ArgumentException>(() => HandlerRows.Saved<BeforeSaveOnly, Memo>(db, Modified()));
        });
    }

    /// <summary>A handler with a before-save stage only.</summary>
    public sealed class BeforeSaveOnly : IBeforeSaveHandler<Memo>
    {
        public ValueTask BeforeSaveAsync(PendingRows<Memo> rows, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Rows_that_a_test_builds_hold_what_they_were_built_with()
    {
        await using var host = SavePipelineHost.Start();

        await host.InScopeAsync(db =>
        {
            var rows = HandlerRows.Saved(db,
                new ChangedRow<Order>(new Order { Name = "new" }, RowChange.Added),
                new ChangedRow<Order>(new Order { Name = "after", Status = "Resolved", IsDeleted = true }, RowChange.Modified, new Order { Name = "before", Status = "New" }),
                new ChangedRow<Order>(new Order { Name = "gone" }, RowChange.Deleted, new Order { Name = "gone" }));

            Assert.Same(db, rows.Db);
            Assert.Equal(3, rows.Count);
            Assert.Null(rows[0].Original);
            Assert.Equal("before", rows[1].Original?.Name);
            Assert.Equal([RowChange.Added, RowChange.Modified, RowChange.Deleted], rows.Select(row => row.Change));

            Assert.Equal([false, true, false], rows.Select(row => row.IsSoftDelete));
            Assert.Equal([false, true, false], rows.Select(row => row.Became(order => order.Status == "Resolved")));
            Assert.Equal([true, false, false], rows.Select(row => row.Became(order => order.Name is not null)));

            rows.Items["key"] = 1;
            Assert.Equal(1, rows.Items["key"]);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task A_change_that_only_the_saves_own_detection_finds_still_comes_with_the_old_values()
    {
        // Nothing has detected the edit when the save starts: the audit backfill, which would, is switched off, as it
        // is for a repository's save after its before-save hooks have run.
        await using var host = SavePipelineHost.Start(services => services.AddSaveHandler<OrderBeforeAndAfter>());

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

    // Resolves every tracked order when a save adds a memo, as an app's own SavingChanges interceptor might.
    public sealed class ResolveTrackedOrders : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var db = (Db)eventData.Context!;

            if (db.Memos.Local.Count > 0)
                foreach (var order in db.Orders.Local.ToList())
                    order.Status = "Resolved";

            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task A_row_that_an_interceptor_changes_during_the_save_comes_with_the_old_values()
    {
        await using var host = SavePipelineHost.Start(
            services => services.AddSaveHandler<OrderBeforeAndAfter>(),
            context => context.AddInterceptors(new ResolveTrackedOrders()));

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
            db.Add(new Memo { Name = "the interceptor resolves the order" });
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(["Modified: 'draft'/'New' -> 'draft'/'Resolved'"], host.Log.Lines);
    }

    [Fact]
    public async Task An_order_whose_owned_value_alone_changed_is_a_modified_row_with_its_old_values()
    {
        // EF writes the owner's row when only an owned value changes, and marks the owner modified while it prepares
        // the write, after the save has started. The owned entry itself is no row: its owner is.
        await using var host = SavePipelineHost.Start(services => services
            .AddSaveHandler<OrderBeforeAndAfter>()
            .AddSaveHandler<CommitLog<object>>());

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

        Assert.Equal(["Modified: 'draft'/'New' -> 'draft'/'New'", "after commit of Object: Modified Order 'draft'"], host.Log.Lines);
    }

    [Fact]
    public async Task Keeping_old_values_runs_no_change_detection_of_its_own()
    {
        // Each row is detected as often as without the handler: by the audit backfill and by EF's own save.
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
        var with = await DetectionsInOneSaveAsync(services => services.AddSaveHandler<LineHandler>());

        Assert.Equal(without, with);
        Assert.Equal(2, with.Order);
    }

    [NeedsOriginal]
    public sealed class LineHandler : IAfterCommitHandler<OrderLine>, IAfterSaveHandler<OrderLine>
    {
        public ValueTask AfterCommitAsync(SavedRows<OrderLine> rows, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask AfterSaveAsync(SavedRows<OrderLine> rows, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    // ── What a row says ─────────────────────────────────────────────────────────

    [NeedsOriginal]
    public sealed class DescribesOrders(SaveLog log) : IAfterCommitHandler<Order>
    {
        public ValueTask AfterCommitAsync(SavedRows<Order> rows, CancellationToken cancellationToken)
        {
            foreach (var row in rows)
                log.Add($"{row.Change} '{row.Entity.Name}': soft delete {row.IsSoftDelete}, became resolved {row.Became(order => order.Status == "Resolved")}");

            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task A_soft_delete_is_a_modified_row_that_says_so_and_a_hard_delete_is_a_deleted_row()
    {
        await using var host = SavePipelineHost.Start(services => services.AddSaveHandler<DescribesOrders>());

        await host.InScopeAsync(async db =>
        {
            var kept = new Order { Name = "kept", Status = "New" };
            var removed = new Order { Name = "removed", Status = "Resolved" };
            db.AddRange(kept, removed);
            await db.SaveChangesAsync(Ct);

            kept.Status = "Resolved";
            await db.SaveChangesAsync(Ct);

            kept.Name = "renamed";             // still resolved: it did not become resolved again
            await db.SaveChangesAsync(Ct);

            kept.IsDeleted = true;
            db.Remove(removed);
            await db.SaveChangesAsync(Ct);

            kept.IsDeleted = false;
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(
        [
            "Added 'kept': soft delete False, became resolved False",
            "Added 'removed': soft delete False, became resolved True",
            "Modified 'kept': soft delete False, became resolved True",
            "Modified 'renamed': soft delete False, became resolved False",
            "Modified 'renamed': soft delete True, became resolved False",
            "Deleted 'removed': soft delete False, became resolved False",
            "Modified 'renamed': soft delete False, became resolved False",
        ], host.Log.Lines);
    }

    [Fact]
    public async Task A_record_attached_with_Update_and_never_loaded_is_a_modified_row_in_which_nothing_became_anything()
    {
        await using var host = SavePipelineHost.Start(services => services.AddSaveHandler<DescribesOrders>());

        var id = await host.InScopeAsync(async db =>
        {
            var order = new Order { Name = "one", Status = "New" };
            db.Add(order);
            await db.SaveChangesAsync(Ct);
            return order.ID;
        });

        host.Log.Clear();

        await host.InScopeAsync(async db =>
        {
            db.Update(new Order { ID = id, Name = "one", Status = "Resolved" });
            await db.SaveChangesAsync(Ct);
        });

        // Original is what the caller passed in.
        Assert.Equal(["Modified 'one': soft delete False, became resolved False"], host.Log.Lines);
    }

    // ── The switches that exist ─────────────────────────────────────────────────

    [Fact]
    public async Task A_save_under_SuppressAllSaveHandlers_runs_no_handler_in_any_stage_also_when_its_transaction_commits_after_the_scope()
    {
        await using var host = SavePipelineHost.Start(services => services
            .AddSaveHandler<StageLog<Memo>>()
            .AddScoped<IShiftDbContextSaveObserver, LoggingSaveObserver>());

        await host.InScopeAsync(async db =>
        {
            using (db.SuppressAllSaveHandlers())
            {
                db.Add(new Memo { Name = "async" });
                await db.SaveChangesAsync(Ct);

                db.Add(new Memo { Name = "sync" });
                db.SaveChanges();
            }

            await using (var transaction = await db.Database.BeginTransactionAsync(Ct))
            {
                using (db.SuppressAllSaveHandlers())
                {
                    db.Add(new Memo { Name = "in a scope" });
                    await db.SaveChangesAsync(Ct);
                }

                await transaction.CommitAsync(Ct);
            }

            db.Add(new Memo { Name = "after" });
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(
        [
            "observer: Added Memo", "observer: Added Memo", "observer: Added Memo", "observer: Added Memo",
            "after save of Memo: Added Memo 'after'",
            "before commit of Memo: Added Memo 'after'",
            "after commit of Memo: Added Memo 'after'",
        ], host.Log.Lines);
    }

    [Fact]
    public async Task Each_switch_silences_only_its_own_part()
    {
        await using var host = SavePipelineHost.Start(services => services
            .AddSaveHandler<CommitLog<Memo>>()
            .AddScoped<IShiftDbContextSaveObserver, LoggingSaveObserver>());

        await host.InScopeAsync(async db =>
        {
            using (db.SuppressReplication())
            {
                db.Add(new Memo { Name = "not replicated" });
                await db.SaveChangesAsync(Ct);
            }

            using (db.SuppressNotifications())
            {
                db.Add(new Memo { Name = "without notifications" });
                await db.SaveChangesAsync(Ct);
            }

            using (db.SuppressReplication())
            using (db.SuppressNotifications())
            using (db.SuppressAllSaveHandlers())
            {
                db.Add(new Memo { Name = "silent" });
                await db.SaveChangesAsync(Ct);
            }
        });

        Assert.Equal(
        [
            "after commit of Memo: Added Memo 'not replicated'",
            "observer: Added Memo",
            "after commit of Memo: Added Memo 'without notifications'",
        ], host.Log.Lines);
    }

    [Fact]
    public async Task The_synchronous_save_runs_every_stage_before_it_returns()
    {
        await using var host = SavePipelineHost.Start(services => services.AddSaveHandler<StageLog<Order>>());

        await host.InScopeAsync(db =>
        {
            db.Add(new Order { Name = "sync" });
            db.SaveChanges();
            host.Log.Add("save returned");
            return Task.CompletedTask;
        });

        Assert.Equal(
        [
            "after save of Order: Added Order 'sync'",
            "before commit of Order: Added Order 'sync'",
            "after commit of Order: Added Order 'sync'",
            "save returned",
        ], host.Log.Lines);
    }
}
