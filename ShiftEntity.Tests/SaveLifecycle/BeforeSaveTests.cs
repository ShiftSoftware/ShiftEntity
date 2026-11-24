using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.Tests.SaveHandlers;
using Xunit;
using static ShiftSoftware.ShiftEntity.Tests.SaveHandlers.SavePipelineModel;
using static ShiftSoftware.ShiftEntity.Tests.SaveLifecycle.ScriptedHandlers;

namespace ShiftSoftware.ShiftEntity.Tests.SaveLifecycle;

/// <summary>
/// The before-save stage: what a handler is called with, the rounds in which the rows that handlers add and change
/// reach the handlers of their own type, what a handler may not do, and the transaction a handler asks for.
/// </summary>
public class BeforeSaveTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Describe<TEntity>(IEnumerable<ChangedRow<TEntity>> rows) where TEntity : class =>
        string.Join(", ", rows.Select(row => $"{row.Change} '{(row.Entity as INamed)?.Name}'").Order(StringComparer.Ordinal));

    // The statements that wrote rows, in an order that does not depend on how EF batched them.
    private static List<string> Statements(SavePipelineHost host) => host.Sql.Writes
        .Where(line => line.StartsWith("INSERT") || line.StartsWith("UPDATE") || line.StartsWith("DELETE"))
        .Order(StringComparer.Ordinal)
        .ToList();

    // ── What a handler is called with ───────────────────────────────────────────

    [Fact]
    public async Task A_handler_is_called_with_the_rows_the_save_is_about_to_write_and_what_it_changes_is_written()
    {
        var calls = new List<string>();

        await using var host = SavePipelineHost.Start(services => services.AddScript(new Script<Order>
        {
            BeforeSave = (rows, _) =>
            {
                calls.Add(Describe(rows));

                foreach (var row in rows)
                    if (row.Change != RowChange.Deleted)
                        row.Entity.Status = $"checked when {row.Change}";

                return Task.CompletedTask;
            },
        }));

        await host.InScopeAsync(async db =>
        {
            var kept = new Order { Name = "kept" };
            var dropped = new Order { Name = "dropped" };
            db.AddRange(kept, dropped);
            await db.SaveChangesAsync(Ct);

            kept.Name = "kept, renamed";
            db.Remove(dropped);
            db.Add(new Memo { Name = "no handler has memos" });
            await db.SaveChangesAsync(Ct);

            // A save with no row of the handler's type does not call it.
            db.Add(new Memo { Name = "another" });
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(["Added 'dropped', Added 'kept'", "Deleted 'dropped', Modified 'kept, renamed'"], calls);

        // The handler ran before the write, so each row was written once, with the handler's change in it.
        Assert.Equal(["DELETE Orders", "INSERT Memos", "INSERT Memos", "INSERT Orders", "INSERT Orders", "UPDATE Orders"], Statements(host));
        Assert.Equal("checked when Modified", await host.InScopeAsync(db => db.Orders.Select(x => x.Status).SingleAsync(Ct)));
    }

    [Fact]
    public async Task The_values_from_before_the_save_are_always_there_and_are_read_from_EF_only_when_asked_for()
    {
        var seen = new List<string>();

        await using var host = SavePipelineHost.Start(services => services.AddScript(new Script<Order>
        {
            BeforeSave = (rows, _) =>
            {
                foreach (var row in rows)
                {
                    var entry = rows.Entry(row);

                    seen.Add($"{row.Change}: '{row.Original?.Name}'/'{row.Original?.Status}' -> '{row.Entity.Name}'/'{row.Entity.Status}', " +
                        $"name modified: {entry.Property(x => x.Name).IsModified}, became shipped: {row.Became(x => x.Status == "shipped")}");
                }

                return Task.CompletedTask;
            },
        }));

        await host.InScopeAsync(async db =>
        {
            var changed = new Order { Name = "a", Status = "new" };
            var removed = new Order { Name = "b", Status = "new" };
            db.AddRange(changed, removed);
            await db.SaveChangesAsync(Ct);
            seen.Clear();

            changed.Status = "shipped";
            db.Remove(removed);
            db.Add(new Order { Name = "c", Status = "shipped" });
            await db.SaveChangesAsync(Ct);

            // Nothing was copied for it: no handler after the write asked for old values.
            Assert.Equal(0, db.OriginalsCopied);
        });

        Assert.Equal(
        [
            "Added: ''/'' -> 'c'/'shipped', name modified: False, became shipped: True",
            "Deleted: 'b'/'new' -> 'b'/'new', name modified: False, became shipped: False",
            "Modified: 'a'/'new' -> 'a'/'shipped', name modified: False, became shipped: True",
        ], seen.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_handler_for_a_base_class_or_an_interface_gets_every_row_that_is_one()
    {
        var named = new List<string>();
        var orders = new List<string>();

        await using var host = SavePipelineHost.Start(services => services
            .AddScript(new Script<INamed> { BeforeSave = (rows, _) => { named.Add(Describe(rows)); return Task.CompletedTask; } })
            .AddScript(new Script<Order> { BeforeSave = (rows, _) => { orders.Add(Describe(rows)); return Task.CompletedTask; } }));

        await host.InScopeAsync(async db =>
        {
            db.Add(new Order { Name = "order" });
            db.Add(new RushOrder { Name = "rush" });
            db.Add(new Memo { Name = "memo" });
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(["Added 'memo', Added 'order', Added 'rush'"], named);
        Assert.Equal(["Added 'order', Added 'rush'"], orders);
    }

    [Fact]
    public async Task A_soft_delete_is_a_modified_row_that_says_so_and_a_hard_delete_is_a_deleted_row_in_every_stage()
    {
        var seen = new SaveLog();

        Task Note<TRows>(string stage, TRows rows) where TRows : RowSet<Order>
        {
            foreach (var row in rows)
                seen.Add($"{stage}: {row.Change} '{row.Entity.Name}', soft delete: {row.IsSoftDelete}");

            return Task.CompletedTask;
        }

        await using var host = SavePipelineHost.Start(services => services.AddScript(new Script<Order>
        {
            BeforeSave = (rows, _) => Note("before save", rows),
            AfterSave = (rows, _) => Note("after save", rows),
            BeforeCommit = (rows, _) => Note("before commit", rows),
            AfterCommit = (rows, _) => Note("after commit", rows),
        }));

        await host.InScopeAsync(async db =>
        {
            var soft = new Order { Name = "soft" };
            var hard = new Order { Name = "hard" };
            db.AddRange(soft, hard);
            await db.SaveChangesAsync(Ct);
            seen.Clear();

            soft.IsDeleted = true;
            await db.SaveChangesAsync(Ct);

            db.Remove(hard);
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(
        [
            "before save: Modified 'soft', soft delete: True",
            "after save: Modified 'soft', soft delete: True",
            "before commit: Modified 'soft', soft delete: True",
            "after commit: Modified 'soft', soft delete: True",
            "before save: Deleted 'hard', soft delete: False",
            "after save: Deleted 'hard', soft delete: False",
            "before commit: Deleted 'hard', soft delete: False",
            "after commit: Deleted 'hard', soft delete: False",
        ], seen.Lines);

        // The soft-deleted row is still there, and the hard-deleted one is gone.
        Assert.Equal(["soft"], await host.InScopeAsync(db => db.Orders.Select(x => x.Name).ToListAsync(Ct)));
    }

    [Fact]
    public async Task An_order_whose_owned_value_alone_changed_is_offered_as_a_modified_row()
    {
        var calls = new List<string>();

        await using var host = SavePipelineHost.Start(services => services.AddScript(new Script<Order>
        {
            BeforeSave = (rows, _) =>
            {
                // EF marks the owner modified only during its write, so its entry still says unchanged here.
                calls.Add(string.Join(", ", rows.Select(row => $"{row.Change} '{row.Entity.Name}' in {row.Entity.Address.City}, entry {rows.Entry(row).State}")));
                return Task.CompletedTask;
            },
        }));

        await host.InScopeAsync(async db =>
        {
            var moved = new Order { Name = "moved", Address = new Address { City = "Erbil" } };
            var stays = new Order { Name = "stays", Address = new Address { City = "Duhok" } };
            db.AddRange(moved, stays);
            await db.SaveChangesAsync(Ct);
            calls.Clear();

            moved.Address.City = "Sulaymaniyah";
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(["Modified 'moved' in Sulaymaniyah, entry Unchanged"], calls);
    }

    // ── Rounds ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_row_that_a_handler_adds_reaches_the_handlers_of_its_type_in_the_next_round()
    {
        await using var host = SavePipelineHost.Start(services => services
            .AddScript(new Script<Memo>
            {
                // Round 1: the memo's handler adds an order.
                BeforeSave = (rows, _) =>
                {
                    foreach (var row in rows)
                        rows.Db.Add(new Order { Name = "order of " + row.Entity.Name });

                    return Task.CompletedTask;
                },
            })
            .AddScript(new Script<Order>
            {
                // Round 2: the order's handler adds a line.
                BeforeSave = (rows, _) =>
                {
                    foreach (var row in rows)
                        rows.Db.Add(new OrderLine { Order = row.Entity, Note = "line of " + row.Entity.Name });

                    return Task.CompletedTask;
                },
            })
            // Round 3: the line's handler runs. Nothing is new after it, so the save writes.
            .AddScript(new Script<OrderLine> { BeforeSave = NothingBefore }));

        await host.InScopeAsync(async db =>
        {
            db.Add(new Memo { Name = "memo" });
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(
        [
            "before save of Memo: Added Memo 'memo'",
            "before save of Order: Added Order 'order of memo'",
            "before save of OrderLine: Added OrderLine ''",
        ], host.Log.Lines);

        // One write for all three rows.
        Assert.Equal(["INSERT Lines", "INSERT Memos", "INSERT Orders"], Statements(host));
        Assert.Equal("line of order of memo", await host.InScopeAsync(db => db.Lines.Select(x => x.Note).SingleAsync(Ct)));
    }

    // Marks every order it is offered, with a higher order so that it runs after the first handler.
    public sealed class Marker(SaveLog log) : IBeforeSaveHandler<Order>
    {
        public ValueTask BeforeSaveAsync(PendingRows<Order> rows, CancellationToken cancellationToken)
        {
            log.Add("marker: " + Describe(rows));

            foreach (var row in rows)
                if (row.Change != RowChange.Deleted)
                    row.Entity.Status += " marked";

            return ValueTask.CompletedTask;
        }
    }

    public sealed class First(SaveLog log) : IBeforeSaveHandler<Order>
    {
        public ValueTask BeforeSaveAsync(PendingRows<Order> rows, CancellationToken cancellationToken)
        {
            log.Add("first: " + Describe(rows));
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task A_handler_is_called_once_for_a_row_and_kind_of_change_and_not_again_when_a_later_handler_changes_that_row()
    {
        await using var host = SavePipelineHost.Start(services => services
            .AddSaveHandler<First>(order: 1)
            .AddSaveHandler<Marker>(order: 2));

        await host.InScopeAsync(async db =>
        {
            var order = new Order { Name = "a", Status = "new" };
            db.Add(order);
            await db.SaveChangesAsync(Ct);

            order.Name = "b";
            await db.SaveChangesAsync(Ct);
        });

        // The marker changed a row that the first handler had been offered already, as added and then as modified.
        // The first handler is not called for it again: a later handler does what the row needs itself.
        Assert.Equal(["first: Added 'a'", "marker: Added 'a'", "first: Modified 'b'", "marker: Modified 'b'"], host.Log.Lines);
        Assert.Equal("new marked marked", await host.InScopeAsync(db => db.Orders.Select(x => x.Status).SingleAsync(Ct)));
    }

    [Fact]
    public async Task A_row_that_was_offered_as_modified_and_is_then_deleted_by_a_handler_is_offered_again_as_deleted()
    {
        await using var host = SavePipelineHost.Start(services => services
            .AddScript(new Script<Order>
            {
                BeforeSave = (rows, _) =>
                {
                    // Cancelled orders are deleted, whoever cancels them.
                    foreach (var row in rows)
                        if (row.Change == RowChange.Modified && row.Entity.Status == "cancelled")
                            rows.Db.Remove(row.Entity);

                    return Task.CompletedTask;
                },
            }));

        await host.InScopeAsync(async db =>
        {
            var order = new Order { Name = "a", Status = "new" };
            db.Add(order);
            await db.SaveChangesAsync(Ct);

            order.Status = "cancelled";
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(
        [
            "before save of Order: Added Order 'a'",
            "before save of Order: Modified Order 'a'",
            "before save of Order: Deleted Order 'a'",
        ], host.Log.Lines);
        Assert.Equal(0, await host.InScopeAsync(db => db.Orders.CountAsync(Ct)));
    }

    [Fact]
    public async Task Handlers_that_produce_a_new_row_in_every_round_are_stopped_after_16_rounds_and_the_types_are_named()
    {
        var rounds = 0;

        await using var host = SavePipelineHost.Start(services => services.AddScript(new Script<Memo>
        {
            BeforeSave = (rows, _) =>
            {
                rounds++;
                rows.Db.Add(new Memo { Name = "memo " + (rounds + 1) });
                return Task.CompletedTask;
            },
        }));

        await host.InScopeAsync(async db =>
        {
            db.Add(new Memo { Name = "memo 1" });

            var loop = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
            Assert.Contains("still producing rows after 16 rounds: Memo", loop.Message);
            Assert.Contains("OnBeforeSave`1 (before save, Memo)", loop.Message);
        });

        // The handler ran in each of the 16 rounds, and nothing was written.
        Assert.Equal(16, rounds);
        Assert.Empty(host.Sql.Lines);
        Assert.Equal(0, await host.InScopeAsync(db => db.Memos.CountAsync(Ct)));
    }

    [Fact]
    public async Task A_row_that_is_added_and_removed_again_before_the_write_reaches_no_later_stage()
    {
        await using var host = SavePipelineHost.Start(services => services
            .AddScript(new Script<Order>
            {
                BeforeSave = (rows, _) =>
                {
                    foreach (var row in rows)
                        rows.Db.Add(new Memo { Name = row.Entity.Name == "keep" ? "kept memo" : "dropped memo" });

                    return Task.CompletedTask;
                },
            })
            .AddScript(new Script<Memo>
            {
                BeforeSave = (rows, _) =>
                {
                    foreach (var row in rows)
                        if (row.Entity.Name == "dropped memo")
                            rows.Entry(row).State = EntityState.Detached;

                    return Task.CompletedTask;
                },
                AfterSave = Nothing,
                AfterCommit = Nothing,
            }));

        await host.InScopeAsync(async db =>
        {
            db.AddRange(new Order { Name = "keep" }, new Order { Name = "drop" });
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(["after save of Memo: Added Memo 'kept memo'", "after commit of Memo: Added Memo 'kept memo'"],
            host.Log.Lines.Where(line => line.StartsWith("after")));
        Assert.Equal(["kept memo"], await host.InScopeAsync(db => db.Memos.Select(x => x.Name).ToListAsync(Ct)));
    }

    // Fails the first write it sees.
    public sealed class FailsTheFirstWrite : SaveChangesInterceptor
    {
        private bool failed;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (failed)
                return ValueTask.FromResult(result);

            failed = true;
            throw new TimeoutException("The first write failed.");
        }
    }

    [Fact]
    public async Task A_save_that_is_tried_again_after_it_failed_runs_the_handlers_again_and_a_handler_that_checks_adds_nothing_twice()
    {
        await using var host = SavePipelineHost.Start(
            services => services.AddScript(new Script<Order>
            {
                BeforeSave = (rows, _) =>
                {
                    foreach (var row in rows)
                    {
                        var name = "memo of " + row.Entity.Name;

                        // Safe to repeat: the memo of the first attempt is still tracked.
                        if (!rows.Db.ChangeTracker.Entries<Memo>().Any(memo => memo.Entity.Name == name))
                            rows.Db.Add(new Memo { Name = name });
                    }

                    return Task.CompletedTask;
                },
            }),
            context => context.AddInterceptors(new FailsTheFirstWrite()));

        await host.InScopeAsync(async db =>
        {
            db.Add(new Order { Name = "a" });

            await Assert.ThrowsAsync<TimeoutException>(() => db.SaveChangesAsync(Ct));
            Assert.Empty(host.Sql.Writes);

            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(["before save of Order: Added Order 'a'", "before save of Order: Added Order 'a'"], host.Log.Lines);
        Assert.Equal(["memo of a"], await host.InScopeAsync(db => db.Memos.Select(x => x.Name).ToListAsync(Ct)));
    }

    // ── Stamping comes first ────────────────────────────────────────────────────

    [Fact]
    public async Task A_handler_sees_stamped_rows_and_the_rows_it_adds_are_stamped_in_the_next_round()
    {
        var stamped = new List<string>();

        await using var host = SavePipelineHost.Start(services => services
            .AddScript(new Script<Order>
            {
                BeforeSave = (rows, _) =>
                {
                    foreach (var row in rows)
                    {
                        stamped.Add($"order stamped: {row.Entity.CreateDate != default}");
                        rows.Db.Add(new Memo { Name = "memo" });
                    }

                    return Task.CompletedTask;
                },
            })
            .AddScript(new Script<Memo>
            {
                BeforeSave = (rows, _) =>
                {
                    stamped.AddRange(rows.Select(row => $"memo stamped: {row.Entity.CreateDate != default}"));
                    return Task.CompletedTask;
                },
            }));

        await host.InScopeAsync(async db =>
        {
            db.Add(new Order { Name = "a" });
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(["order stamped: True", "memo stamped: True"], stamped);

        var memo = await host.InScopeAsync(db => db.Memos.SingleAsync(Ct));
        Assert.NotEqual(default, memo.CreateDate);
        Assert.Equal(memo.CreateDate, memo.LastSaveDate);
    }

    // ── What a handler may not do ───────────────────────────────────────────────

    [Fact]
    public async Task Saving_from_a_before_save_handler_is_refused_and_nothing_is_written()
    {
        await using var host = SavePipelineHost.Start(services => services.AddScript(new Script<Order>
        {
            // It would write the tracked rows first, and leave the save in progress with nothing.
            BeforeSave = (rows, ct) => rows.Db.SaveChangesAsync(ct),
        }));

        await host.InScopeAsync(async db =>
        {
            var order = new Order { Name = "a" };
            db.Add(order);

            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
            Assert.Contains("cannot save from inside the before-save stage of a save that is in progress on it (OnBeforeSave`1 (before save, Order))", refused.Message);
            Assert.Contains("another context", refused.Message);
            Assert.Throws<InvalidOperationException>(() => db.SaveChanges());

            Assert.Equal(EntityState.Added, db.Entry(order).State);
        });

        Assert.Empty(host.Sql.Lines);
    }

    [Fact]
    public async Task Beginning_a_transaction_from_a_before_save_handler_is_refused_and_points_at_RequireTransactionAsync()
    {
        await using var host = SavePipelineHost.Start(services => services.AddScript(new Script<Order>
        {
            BeforeSave = async (rows, ct) => await rows.Db.Database.BeginTransactionAsync(ct),
        }));

        await host.InScopeAsync(async db =>
        {
            db.Add(new Order { Name = "a" });

            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
            Assert.Contains("cannot begin a transaction from inside a save that is in progress on it", refused.Message);
            Assert.Contains("RequireTransactionAsync", refused.Message);
        });

        Assert.Empty(host.Sql.Lines);
    }

    // Saves the context again as EF's own save starts, as a trigger library's handler did.
    public sealed class SavesAgainInsideTheWrite : SaveChangesInterceptor
    {
        private bool saving;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (saving)
                return result;

            saving = true;

            try
            {
                await eventData.Context!.SaveChangesAsync(cancellationToken);
            }
            finally
            {
                saving = false;
            }

            return result;
        }
    }

    [Fact]
    public async Task Saving_from_inside_EFs_own_write_is_refused()
    {
        await using var host = SavePipelineHost.Start(context: context => context.AddInterceptors(new SavesAgainInsideTheWrite()));

        await host.InScopeAsync(async db =>
        {
            var order = new Order { Name = "a" };
            db.Add(order);

            // The inner save would write the rows of the save in progress, and the outer save would write none.
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
            Assert.Contains("cannot save from inside EF's own write of a save that is in progress on it", refused.Message);
            Assert.Equal(EntityState.Added, db.Entry(order).State);
        });

        Assert.Empty(host.Sql.Writes);
    }

    // ── A transaction that a handler asks for ───────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequireTransactionAsync_opens_the_contexts_transaction_before_the_handlers_query_and_the_save_commits_it(bool sqlServer)
    {
        var inside = "";

        await using var host = await SavePipelineHost.StartAsync(sqlServer, "RequireTransaction", services => services.AddScript(new Script<Order>
        {
            BeforeSave = async (rows, ct) =>
            {
                var before = rows.Db.Database.CurrentTransaction is not null;
                await rows.RequireTransactionAsync(ct);

                // Asking again does nothing: the transaction is open.
                await rows.RequireTransactionAsync(ct);

                inside = $"open before: {before}, open after: {rows.Db.Database.CurrentTransaction is not null}, memos: {await rows.Db.Set<Memo>().CountAsync(ct)}";
            },
            AfterCommit = Nothing,
        }));

        await host.InScopeAsync(async db =>
        {
            db.Add(new Order { Name = "a" });
            await db.SaveChangesAsync(Ct);

            Assert.Null(db.Database.CurrentTransaction);
        });

        // Begin, the handler's query, the write, commit: one transaction.
        Assert.Equal(["BEGIN", "SELECT", "INSERT Orders", "COMMIT"], host.Sql.Lines);
        Assert.Equal("open before: False, open after: True, memos: 0", inside);
        Assert.Equal(
        [
            "before save of Order: Added Order 'a'",
            "after commit of Order: Added Order 'a'",
        ], host.Log.Lines);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task With_only_before_save_and_after_commit_handlers_a_save_opens_no_transaction(bool sqlServer)
    {
        await using var host = await SavePipelineHost.StartAsync(sqlServer, "NoTransactionBefore", services => services.AddScript(new Script<Order>
        {
            BeforeSave = async (rows, ct) => _ = await rows.Db.Set<Memo>().CountAsync(ct),
            AfterCommit = Nothing,
        }));

        await host.InScopeAsync(async db =>
        {
            db.Add(new Order { Name = "a" });
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(["SELECT", "INSERT Orders"], host.Sql.Lines);
    }

    [Fact]
    public async Task Inside_a_transaction_the_app_began_RequireTransactionAsync_opens_none_and_sets_the_saves_one_savepoint()
    {
        await using var host = SavePipelineHost.Start(services => services.AddScript(new Script<Order>
        {
            BeforeSave = async (rows, ct) =>
            {
                await rows.RequireTransactionAsync(ct);
                await rows.RequireTransactionAsync(ct);
            },
        }));

        await host.InScopeAsync(async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);

            db.Add(new Order { Name = "a" });
            await db.SaveChangesAsync(Ct);

            Assert.Same(transaction, db.Database.CurrentTransaction);
            await transaction.CommitAsync(Ct);
        });

        // The framework's savepoint, set where the handler asked, takes the place of EF's: one savepoint, as for any save
        // in a transaction.
        Assert.Equal(["BEGIN", "SAVEPOINT", "INSERT Orders", "COMMIT"], host.Sql.Lines);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Inside_the_apps_transaction_a_failed_save_undoes_the_SQL_a_handler_ran_once_it_asked_for_a_transaction(bool sqlServer)
    {
        // A counter that a before-save handler moves on with SQL of its own, the other common shape of numbering.
        await using var host = await SavePipelineHost.StartAsync(sqlServer, "CounterUndone", services => services.AddScript(new Script<Order>
        {
            BeforeSave = async (rows, ct) =>
            {
                await rows.RequireTransactionAsync(ct);
                await rows.Db.Database.ExecuteSqlRawAsync("UPDATE Tickets SET Number = Number + 1 WHERE Queue = 'counter'", ct);
            },
            AfterSave = (rows, _) => rows.Any(row => row.Entity.Name == "fails") ? throw new InvalidOperationException("The order is refused.") : Task.CompletedTask,
        }));

        await using (var seed = host.Bare())
        {
            seed.Add(new Ticket { Queue = "counter", Name = "counter" });
            await seed.SaveChangesAsync(Ct);
        }

        host.Sql.Clear();

        await host.InScopeAsync(async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);

            // The save fails after its write. Rolled back to the savepoint set where the handler asked, it takes the
            // counter's step back with it.
            db.Add(new Order { Name = "fails" });
            Assert.Equal("The order is refused.", (await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct))).Message);

            // The app's transaction goes on, and commits.
            db.ChangeTracker.Clear();
            db.Add(new Order { Name = "works" });
            await db.SaveChangesAsync(Ct);

            await transaction.CommitAsync(Ct);
        });

        // One savepoint for each save: the write used the handler's and set no other.
        Assert.Equal(
        [
            "BEGIN",
            "SAVEPOINT", "UPDATE Tickets", "INSERT Orders", "ROLLBACK TO SAVEPOINT",
            "SAVEPOINT", "UPDATE Tickets", "INSERT Orders",
            "COMMIT",
        ], host.Sql.Writes);

        Assert.Equal(1, await host.InScopeAsync(db => db.Tickets.Where(x => x.Queue == "counter").Select(x => x.Number).SingleAsync(Ct)));
        Assert.Equal(["works"], await host.InScopeAsync(db => db.Orders.Select(x => x.Name).ToListAsync(Ct)));
    }

    // ── A save that fails before its write, once a handler moved a counter on ───

    // A handler that moves a counter on with SQL of its own, asking for a transaction first or not, and then refuses an
    // order named "refused": the save fails before anything of it is written.
    private static Script<Order> CounterStepThenARefusal(bool asks) => new()
    {
        BeforeSave = async (rows, ct) =>
        {
            if (asks)
                await rows.RequireTransactionAsync(ct);

            await rows.Db.Database.ExecuteSqlRawAsync("UPDATE Tickets SET Number = Number + 1 WHERE Queue = 'counter'", ct);

            if (rows.Any(row => row.Entity.Name == "refused"))
                throw new InvalidOperationException("The order is refused before its write.");
        },
    };

    private static async Task SeedCounterAsync(SavePipelineHost host)
    {
        await using (var seed = host.Bare())
        {
            seed.Add(new Ticket { Queue = "counter", Name = "counter" });
            await seed.SaveChangesAsync(Ct);
        }

        host.Sql.Clear();
    }

    private static Task<int> CounterAsync(SavePipelineHost host) =>
        host.InScopeAsync(db => db.Tickets.Where(x => x.Queue == "counter").Select(x => x.Number).SingleAsync(Ct));

    // Inside the app's transaction: a save that is refused before its write, then one that works, then the commit.
    // Returns the commit's failure, or null when it committed.
    private static Task<Exception?> RefusedThenWorksThenCommitAsync(SavePipelineHost host, bool savepointsOff) => host.InScopeAsync(async db =>
    {
        if (savepointsOff)
            db.Database.AutoSavepointsEnabled = false;

        await using var transaction = await db.Database.BeginTransactionAsync(Ct);

        db.Add(new Order { Name = "refused" });
        Assert.Equal("The order is refused before its write.", (await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct))).Message);

        db.ChangeTracker.Clear();
        db.Add(new Order { Name = "works" });
        await db.SaveChangesAsync(Ct);

        try
        {
            await transaction.CommitAsync(Ct);
            return (Exception?)null;
        }
        catch (Exception failure)
        {
            return failure;
        }
    });

    [Theory]
    [InlineData("SQL Server with MARS")]
    [InlineData("savepoints off")]
    public async Task Where_no_savepoint_can_be_set_a_save_that_asked_for_a_transaction_and_fails_before_its_write_leaves_the_apps_transaction_only_to_roll_back(string where)
    {
        // The handler asked for a transaction and moved the counter on, then refused the save. Nothing of the save was
        // written, but with no savepoint the counter's step cannot be undone alone: the transaction can only be rolled
        // back, as after a failure after the write.
        var script = CounterStepThenARefusal(asks: true);

        await using var host = where == "savepoints off"
            ? SavePipelineHost.Start(services => services.AddScript(script))
            : await SavePipelineHost.StartOnSqlServerAsync("CounterWithoutSavepoint", services => services.AddScript(script),
                connectionString: connection => connection + ";MultipleActiveResultSets=True");

        await SeedCounterAsync(host);

        var commit = await RefusedThenWorksThenCommitAsync(host, savepointsOff: where == "savepoints off");

        Assert.DoesNotContain("SAVEPOINT", host.Sql.Writes);
        Assert.Contains("can only be rolled back", Assert.IsType<InvalidOperationException>(commit).Message);
        Assert.Contains("RequireTransactionAsync", commit.Message);

        // Rolled back whole: neither save's counter step, nor the order that was saved after the refusal.
        Assert.Equal(0, await CounterAsync(host));
        Assert.Equal(0, await host.InScopeAsync(db => db.Orders.CountAsync(Ct)));
    }

    [Fact]
    public async Task In_an_ambient_transaction_a_save_that_asked_for_a_transaction_and_fails_before_its_write_rolls_it_back()
    {
        // No savepoint can be set in an ambient transaction. The context rolls it back, and the scope can no longer
        // commit the counter's step.
        await using var host = await SavePipelineHost.StartOnSqlServerAsync("CounterAmbient", services => services.AddScript(CounterStepThenARefusal(asks: true)));

        await SeedCounterAsync(host);

        await Assert.ThrowsAsync<System.Transactions.TransactionAbortedException>(async () =>
        {
            using var ambient = new System.Transactions.TransactionScope(System.Transactions.TransactionScopeAsyncFlowOption.Enabled);

            await host.InScopeAsync(async db =>
            {
                db.Add(new Order { Name = "refused" });
                Assert.Equal("The order is refused before its write.", (await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct))).Message);
            });

            // The caller swallowed the failure and completes the scope all the same.
            ambient.Complete();
        });

        Assert.Equal(0, await CounterAsync(host));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Where_a_savepoint_can_be_set_a_save_that_asked_for_a_transaction_and_fails_before_its_write_rolls_back_to_it_and_the_apps_transaction_commits(bool sqlServer)
    {
        await using var host = await SavePipelineHost.StartAsync(sqlServer, "CounterWithSavepoint", services => services.AddScript(CounterStepThenARefusal(asks: true)));

        await SeedCounterAsync(host);

        Assert.Null(await RefusedThenWorksThenCommitAsync(host, savepointsOff: false));

        // The refused save's step went with its savepoint. The saved order's step is committed.
        Assert.Equal(["BEGIN", "SAVEPOINT", "UPDATE Tickets", "ROLLBACK TO SAVEPOINT", "SAVEPOINT", "UPDATE Tickets", "INSERT Orders", "COMMIT"], host.Sql.Writes);
        Assert.Equal(1, await CounterAsync(host));
        Assert.Equal(["works"], await host.InScopeAsync(db => db.Orders.Select(x => x.Name).ToListAsync(Ct)));
    }

    [Theory]
    [InlineData("SQL Server with MARS")]
    [InlineData("savepoints off")]
    public async Task Where_no_savepoint_can_be_set_a_save_that_did_not_ask_for_a_transaction_and_fails_before_its_write_leaves_the_apps_transaction_usable(string where)
    {
        // A handler that did not ask has no part of its SQL covered: the save marks nothing, the app's transaction
        // commits, and the refused save's counter step is committed with it.
        var script = CounterStepThenARefusal(asks: false);

        await using var host = where == "savepoints off"
            ? SavePipelineHost.Start(services => services.AddScript(script))
            : await SavePipelineHost.StartOnSqlServerAsync("CounterNotAsked", services => services.AddScript(script),
                connectionString: connection => connection + ";MultipleActiveResultSets=True");

        await SeedCounterAsync(host);

        Assert.Null(await RefusedThenWorksThenCommitAsync(host, savepointsOff: where == "savepoints off"));

        Assert.Equal(2, await CounterAsync(host));
        Assert.Equal(["works"], await host.InScopeAsync(db => db.Orders.Select(x => x.Name).ToListAsync(Ct)));
    }

    [Fact]
    public async Task A_save_that_fails_after_a_handler_asked_for_a_transaction_rolls_it_back()
    {
        await using var host = SavePipelineHost.Start(services => services
            .AddScript(new Script<Order> { BeforeSave = async (rows, ct) => await rows.RequireTransactionAsync(ct) })
            .AddScript(new Script<Memo> { BeforeSave = (_, _) => throw new InvalidOperationException("The memo is refused.") }));

        await host.InScopeAsync(async db =>
        {
            db.Add(new Order { Name = "a" });
            db.Add(new Memo { Name = "refused" });

            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
            Assert.Equal("The memo is refused.", refused.Message);
            Assert.Null(db.Database.CurrentTransaction);

            // Nothing was written, so the context holds no rows of an undone save: the caller may fix and save again.
            db.Entry(db.ChangeTracker.Entries<Memo>().Single().Entity).State = EntityState.Detached;
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(["BEGIN", "ROLLBACK", "BEGIN", "INSERT Orders", "COMMIT"], host.Sql.Lines);
    }

    // Retries a timeout, as EnableRetryOnFailure does a transient SQL error.
    public sealed class RetryingStrategy(ExecutionStrategyDependencies dependencies)
        : ExecutionStrategy(dependencies, 3, TimeSpan.FromMilliseconds(5))
    {
        protected override bool ShouldRetryOn(Exception exception) => exception is TimeoutException;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequireTransactionAsync_is_refused_where_the_context_cannot_open_a_transaction_of_its_own(bool retrying)
    {
        await using var host = SavePipelineHost.Start(
            services => services.AddScript(new Script<Order> { BeforeSave = async (rows, ct) => await rows.RequireTransactionAsync(ct) }),
            sqlite: retrying ? sqlite => sqlite.ExecutionStrategy(dependencies => new RetryingStrategy(dependencies)) : null);

        await host.InScopeAsync(async db =>
        {
            if (!retrying)
                db.Database.AutoTransactionBehavior = AutoTransactionBehavior.Never;

            db.Add(new Order { Name = "a" });

            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
            Assert.Contains("cannot save rows that OnBeforeSave`1 asked for a transaction around (RequireTransactionAsync)", refused.Message);
            Assert.Contains(retrying ? "retries on failure (RetryingStrategy)" : "AutoTransactionBehavior.Never", refused.Message);
        });

        Assert.Empty(host.Sql.Lines);
    }

    [Fact]
    public async Task RequireTransactionAsync_on_rows_that_a_test_built_does_nothing_and_outside_its_handler_it_is_refused()
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
            // A unit test of a handler: no save is in progress, and nothing is opened.
            var built = HandlerRows.Pending(db, new ChangedRow<Order>(new Order { Name = "a" }, RowChange.Added));
            await built.RequireTransactionAsync(Ct);
            Assert.Null(db.Database.CurrentTransaction);
            Assert.False(built.IsRepositorySave);
            Assert.Equal(EntityState.Detached, built.Entry(built[0]).State);

            db.Add(new Order { Name = "b" });
            await db.SaveChangesAsync(Ct);

            // The rows of a save belong to the handler call they were made for.
            var late = await Assert.ThrowsAsync<InvalidOperationException>(() => kept!.RequireTransactionAsync(Ct).AsTask());
            Assert.Contains("only by that handler, while it runs", late.Message);
        });
    }

    // ── A number under a lock ───────────────────────────────────────────────────

    /// <summary>
    /// Gives each new ticket the next number of its queue, under a lock that the save's transaction holds until it
    /// commits.
    /// </summary>
    public sealed class TicketNumbering : IBeforeSaveHandler<Ticket>
    {
        public async ValueTask BeforeSaveAsync(PendingRows<Ticket> rows, CancellationToken cancellationToken)
        {
            var added = rows.Where(row => row.Change == RowChange.Added).Select(row => row.Entity);

            // In a fixed order, so that two saves that number several queues do not wait for each other.
            foreach (var queue in added.GroupBy(ticket => ticket.Queue).OrderBy(queue => queue.Key, StringComparer.Ordinal))
            {
                // One transaction at a time numbers a queue, until it commits.
                await rows.TakeLockAsync($"TicketNumbering:{queue.Key}", cancellationToken);

                // The last number is the table's, or the one given out in an earlier round of this save, which is not
                // written yet.
                var kept = (typeof(TicketNumbering), queue.Key);
                var last = Math.Max(await LastNumberAsync(rows.Db, queue.Key, cancellationToken), rows.Items.TryGetValue(kept, out var given) ? (int)given! : 0);

                foreach (var ticket in queue)
                    ticket.Number = ++last;

                rows.Items[kept] = last;
            }
        }

        private static async Task<int> LastNumberAsync(ShiftDbContext db, string queue, CancellationToken cancellationToken) =>
            await db.Set<Ticket>().IgnoreQueryFilters().Where(ticket => ticket.Queue == queue)
                .MaxAsync(ticket => (int?)ticket.Number, cancellationToken) ?? 0;
    }

    /// <summary>Adds a second ticket to the queue of a ticket that asks for one, so that a later round has rows to number.</summary>
    public sealed class TicketTwins : IBeforeSaveHandler<Ticket>
    {
        public ValueTask BeforeSaveAsync(PendingRows<Ticket> rows, CancellationToken cancellationToken)
        {
            foreach (var row in rows)
                if (row.Change == RowChange.Added && row.Entity.Name == "with a twin")
                    rows.Db.Add(new Ticket { Queue = row.Entity.Queue, Name = "twin" });

            return ValueTask.CompletedTask;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_numbering_handler_numbers_the_rows_of_every_round_and_of_every_save_of_a_transaction_without_a_gap(bool sqlServer)
    {
        await using var host = await SavePipelineHost.StartAsync(sqlServer, "Numbering",
            services => services.AddSaveHandler<TicketNumbering>().AddSaveHandler<TicketTwins>());

        await host.InScopeAsync(async db =>
        {
            // One save, two rounds: the twin appears in the first round and is numbered in the second.
            db.AddRange(new Ticket { Queue = "a", Name = "first" }, new Ticket { Queue = "a", Name = "with a twin" }, new Ticket { Queue = "b", Name = "other queue" });
            await db.SaveChangesAsync(Ct);

            // Two saves in one transaction: the second carries on where the first stopped.
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            db.Add(new Ticket { Queue = "a", Name = "in a transaction" });
            await db.SaveChangesAsync(Ct);
            db.Add(new Ticket { Queue = "a", Name = "in the same transaction" });
            await db.SaveChangesAsync(Ct);
            await transaction.CommitAsync(Ct);

            // A later save reads the table again.
            db.Add(new Ticket { Queue = "a", Name = "later" });
            await db.SaveChangesAsync(Ct);
        });

        var numbers = await host.InScopeAsync(db => db.Tickets.OrderBy(x => x.Queue).ThenBy(x => x.Number).Select(x => $"{x.Queue}{x.Number} {x.Name}").ToListAsync(Ct));

        Assert.Equal(6, numbers.Count(line => line.StartsWith('a')));
        Assert.Equal(["a1", "a2", "a3", "a4", "a5", "a6", "b1"], numbers.Select(line => line.Split(' ')[0]));
        Assert.Equal("a4 in a transaction", numbers[3]);
        Assert.Equal("a5 in the same transaction", numbers[4]);
        Assert.Equal("a6 later", numbers[5]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Inside_a_transaction_a_failed_save_and_a_rollback_to_a_savepoint_give_their_numbers_to_the_next_save(bool sqlServer)
    {
        // The numbering keeps the next number in the unit's bag. A save that fails, and a rollback to a savepoint of the
        // app's own, put the bag back as it was where they began, so the next save takes the numbers that were given
        // back, and none is left out.
        await using var host = await SavePipelineHost.StartAsync(sqlServer, "NumbersGivenBack", services => services
            .AddSaveHandler<TicketNumbering>()
            .AddScript(new Script<Ticket>
            {
                AfterSave = (rows, _) => rows.Any(row => row.Entity.Name == "fails") ? throw new InvalidOperationException("The ticket is refused.") : Task.CompletedTask,
            }));

        await host.InScopeAsync(async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);

            db.Add(new Ticket { Queue = "a", Name = "first" });
            await db.SaveChangesAsync(Ct);

            // Numbered, then refused after its write.
            var refused = new Ticket { Queue = "a", Name = "fails" };
            db.Add(refused);
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
            Assert.Equal(2, refused.Number);

            db.ChangeTracker.Clear();
            db.Add(new Ticket { Queue = "a", Name = "second" });
            await db.SaveChangesAsync(Ct);

            // A save after a savepoint of the app's own, rolled back to it.
            await transaction.CreateSavepointAsync("before the third", Ct);

            var rolledBack = new Ticket { Queue = "a", Name = "rolled back" };
            db.Add(rolledBack);
            await db.SaveChangesAsync(Ct);
            Assert.Equal(3, rolledBack.Number);

            await transaction.RollbackToSavepointAsync("before the third", Ct);

            db.ChangeTracker.Clear();
            db.Add(new Ticket { Queue = "a", Name = "third" });
            await db.SaveChangesAsync(Ct);

            await transaction.CommitAsync(Ct);
        });

        Assert.Equal(["a1 first", "a2 second", "a3 third"],
            await host.InScopeAsync(db => db.Tickets.OrderBy(x => x.Number).Select(x => $"{x.Queue}{x.Number} {x.Name}").ToListAsync(Ct)));
    }

    public enum NumberingOn
    {
        SqlServer,
        SqlServerReadingCommittedSnapshots,     // as Azure SQL does unless it is told otherwise
        SqliteFile,
    }

    [Theory]
    [InlineData(NumberingOn.SqlServer)]
    [InlineData(NumberingOn.SqlServerReadingCommittedSnapshots)]
    [InlineData(NumberingOn.SqliteFile)]
    public async Task Concurrent_saves_that_number_under_a_lock_give_out_no_number_twice_also_for_rows_of_a_second_round(NumberingOn database)
    {
        const int writers = 6;
        const int savesEach = 15;

        static void Handlers(IServiceCollection services) => services.AddSaveHandler<TicketNumbering>().AddSaveHandler<TicketTwins>();

        // From empty queues. On SQLite the database is a file, so that every writer has a connection of its own.
        await using var host = database == NumberingOn.SqliteFile
            ? SavePipelineHost.StartOnFile(Handlers)
            : await SavePipelineHost.StartOnSqlServerAsync("NumberingConcurrently", Handlers);

        if (database == NumberingOn.SqlServerReadingCommittedSnapshots)
            await host.InScopeAsync(db => db.Database.ExecuteSqlRawAsync("ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;", Ct));

        await Task.WhenAll(Enumerable.Range(0, writers).Select(writer => Task.Run(async () =>
        {
            for (var save = 0; save < savesEach; save++)
            {
                await host.InScopeAsync(async db =>
                {
                    // Three tickets in queue "a", one of them added in the save's second round, and one in "b".
                    db.AddRange(
                        new Ticket { Queue = "a", Name = $"writer {writer}" },
                        new Ticket { Queue = "a", Name = "with a twin" },
                        new Ticket { Queue = "b", Name = $"writer {writer}" });

                    await db.SaveChangesAsync(Ct);
                });
            }
        }, Ct)));

        var a = await host.InScopeAsync(db => db.Tickets.Where(x => x.Queue == "a").Select(x => x.Number).OrderBy(x => x).ToListAsync(Ct));
        var b = await host.InScopeAsync(db => db.Tickets.Where(x => x.Queue == "b").Select(x => x.Number).OrderBy(x => x).ToListAsync(Ct));

        // Every number once, and none left out.
        Assert.Equal(Enumerable.Range(1, writers * savesEach * 3), a);
        Assert.Equal(Enumerable.Range(1, writers * savesEach), b);
    }

    [Fact]
    public async Task While_a_failed_save_of_a_transaction_has_given_its_lock_back_another_may_number_and_the_next_save_goes_on_after_it()
    {
        await using var host = await SavePipelineHost.StartOnSqlServerAsync("NumberingInBetween", services => services
            .AddSaveHandler<TicketNumbering>()
            .AddScript(new Script<Ticket>
            {
                AfterSave = (rows, _) => rows.Any(row => row.Entity.Name == "fails") ? throw new InvalidOperationException("The ticket is refused.") : Task.CompletedTask,
            }));

        // So that the other transaction's read of the last number does not wait for the rows this one has not committed.
        await host.InScopeAsync(db => db.Database.ExecuteSqlRawAsync("ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;", Ct));

        Task AddElsewhereAsync(params Ticket[] tickets) => host.InScopeAsync(async other =>
        {
            other.AddRange(tickets.AsEnumerable());
            await other.SaveChangesAsync(Ct);
        });

        await host.InScopeAsync(async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);

            // The rollback to the failed save's savepoint releases the lock, and puts the bag back.
            db.Add(new Ticket { Queue = "a", Name = "fails" });
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
            db.ChangeTracker.Clear();

            await AddElsewhereAsync(new Ticket { Queue = "a", Name = "in between" });

            db.Add(new Ticket { Queue = "a", Name = "after it" });
            await db.SaveChangesAsync(Ct);

            // A rollback that the framework cannot see releases the lock too, and the bag still holds the number.
            var sql = (SqlTransaction)transaction.GetDbTransaction();
            sql.Save("unseen");
            db.Add(new Ticket { Queue = "b", Name = "rolled back unseen" });
            await db.SaveChangesAsync(Ct);
            sql.Rollback("unseen");
            db.ChangeTracker.Clear();

            await AddElsewhereAsync(new Ticket { Queue = "b", Name = "in between" }, new Ticket { Queue = "b", Name = "in between" });

            db.Add(new Ticket { Queue = "b", Name = "after it" });
            await db.SaveChangesAsync(Ct);

            await transaction.CommitAsync(Ct);
        });

        Assert.Equal(["a1 in between", "a2 after it", "b1 in between", "b2 in between", "b3 after it"],
            await host.InScopeAsync(db => db.Tickets.OrderBy(x => x.Queue).ThenBy(x => x.Number).Select(x => $"{x.Queue}{x.Number} {x.Name}").ToListAsync(Ct)));
    }

    // ── The synchronous save ────────────────────────────────────────────────────

    [Fact]
    public void The_synchronous_save_runs_the_rounds_and_opens_the_transaction_a_handler_asks_for()
    {
        using var host = new Disposable<SavePipelineHost>(SavePipelineHost.Start(services => services
            .AddSaveHandler<TicketNumbering>()
            .AddSaveHandler<TicketTwins>()));

        using var scope = host.Value.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Db>();

        db.Add(new Ticket { Queue = "a", Name = "with a twin" });
        Assert.Equal(2, db.SaveChanges());

        // Each of the two rounds takes the lock and reads the last number.
        Assert.Equal(["BEGIN", "LOCK", "SELECT", "LOCK", "SELECT", "INSERT Tickets", "INSERT Tickets", "COMMIT"], host.Value.Sql.Lines);
        Assert.Equal([1, 2], db.Tickets.Select(x => x.Number).OrderBy(x => x).ToList());
    }

    private sealed class Disposable<T>(T value) : IDisposable where T : IAsyncDisposable
    {
        public T Value => value;

        public void Dispose() => value.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
