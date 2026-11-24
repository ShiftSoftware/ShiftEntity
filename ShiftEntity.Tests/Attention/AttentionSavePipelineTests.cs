using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Attention;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.EFCore.Attention;
using ShiftSoftware.ShiftEntity.EFCore.Entities;
using ShiftSoftware.ShiftEntity.Tests.Attention.Scenario;
using ShiftSoftware.ShiftEntity.Tests.DataLevelAccess.Scenario;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Attention;

/// <summary>
/// Attention in the context's save pipeline. The evaluators run before any save of a <see cref="ShiftDbContext"/>
/// writes an <see cref="IHasAttention"/> row, whoever saves it: a repository, or a service or a function on the plain
/// context. The events are published once the rows are committed. On SQLite, so the transactions are real.
/// </summary>
public class AttentionSavePipelineTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── A host on SQLite ────────────────────────────────────────────────────────

    /// <summary>Records each event as it is published, in place of the channel, so a test sees exactly when.</summary>
    public sealed class RecordingDispatcher(List<string> log) : IAttentionDispatcher
    {
        public List<AttentionRaised> Published { get; } = [];

        public ValueTask PublishAsync(AttentionRaised attentionRaised, CancellationToken cancellationToken = default)
        {
            Published.Add(attentionRaised);
            log.Add($"published {attentionRaised.Signal.Category} of {attentionRaised.EntityType} {attentionRaised.EntityId}");
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Counts the queries that read the signals table, and can fail a chosen write.</summary>
    public sealed class Commands : DbCommandInterceptor, ISaveChangesInterceptor
    {
        public int SignalQueries;
        public int Saves;
        public int? FailSave;

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Count(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Count(command);
            return ValueTask.FromResult(result);
        }

        private void Count(DbCommand command)
        {
            if (command.CommandText.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) && command.CommandText.Contains("\"AttentionSignals\""))
                SignalQueries++;
        }

        public ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (++Saves == FailSave)
                throw new InvalidOperationException("This write was made to fail.");

            return ValueTask.FromResult(result);
        }
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private Host(ServiceProvider provider, SqliteConnection connection)
        {
            Provider = provider;
            this.connection = connection;
        }

        public ServiceProvider Provider { get; }
        public List<string> Log => Provider.GetRequiredService<List<string>>();
        public RecordingDispatcher Dispatcher => Provider.GetRequiredService<RecordingDispatcher>();
        public Commands Commands => Provider.GetRequiredService<Commands>();

        public static Host Start(Action<IServiceCollection>? configure = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<Commands>();
            services.AddDbContext<AttentionTestDbContext>((provider, options) =>
                options.UseSqlite(connection).AddInterceptors(provider.GetRequiredService<Commands>()));

            // What the framework's own registrations contribute, minimally, for a repository to be built.
            services.AddScoped<ICurrentUserProvider>(_ => FakeCurrentUserProvider.Anonymous());
            services.AddScoped<IdentityClaimProvider>();
            services.AddSingleton<IHashIdService>(new RecordingHashIdService());
            services.AddSingleton<IDefaultDataLevelAccess>(new RecordingDefaultDataLevelAccess());

            services.AddSingleton<List<string>>();
            services.AddSingleton<RecordingDispatcher>();
            services.AddSingleton<IAttentionDispatcher>(provider => provider.GetRequiredService<RecordingDispatcher>());
            services.AddAttentionEvaluator<WidgetEntity, WidgetFlagEvaluator>();

            configure?.Invoke(services);

            var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

            using (var scope = provider.CreateScope())
                scope.ServiceProvider.GetRequiredService<AttentionTestDbContext>().Database.EnsureCreated();

            provider.GetRequiredService<Commands>().Saves = 0;

            return new(provider, connection);
        }

        public async Task<T> InScopeAsync<T>(Func<AttentionTestDbContext, Task<T>> work)
        {
            await using var scope = Provider.CreateAsyncScope();
            return await work(scope.ServiceProvider.GetRequiredService<AttentionTestDbContext>());
        }

        public Task InScopeAsync(Func<AttentionTestDbContext, Task> work) => InScopeAsync(async db =>
        {
            await work(db);
            return 0;
        });

        public async ValueTask DisposeAsync()
        {
            await Provider.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private static ShiftRepository<AttentionTestDbContext, GadgetEntity, GadgetDTO, GadgetDTO> GadgetRepository(AttentionTestDbContext db)
        => new(db, o => o.UseMapper(new ThrowingGadgetMapper()));

    // ── Whoever saves ───────────────────────────────────────────────────────────

    [Fact]
    public async Task A_save_on_the_plain_context_flags_a_record_as_a_repository_save_does()
    {
        await using var host = Host.Start();

        await host.InScopeAsync(async db =>
        {
            var gadget = new GadgetEntity { Name = "Anvil", StockLevel = 2 };
            db.Gadgets.Add(gadget);
            await db.SaveChangesAsync(Ct);

            Assert.True(gadget.HasActiveAttention);
            Assert.Equal(1, gadget.ActiveSignalCount);
            Assert.Equal(AttentionSeverity.Warning, gadget.HighestSeverity);

            var published = Assert.Single(host.Dispatcher.Published);
            Assert.Equal(nameof(GadgetEntity), published.EntityType);
            Assert.Equal(gadget.ID, published.EntityId);
            Assert.Equal(GadgetEntity.LowStockCategory, published.Signal.Category);
        });

        // What was stored is what a later load sees.
        await host.InScopeAsync(async db => Assert.True((await db.Gadgets.SingleAsync(Ct)).HasActiveAttention));
    }

    [Fact]
    public async Task An_update_on_the_plain_context_is_evaluated_with_the_values_from_before_it()
    {
        await using var host = Host.Start();

        var id = await host.InScopeAsync(async db =>
        {
            var gadget = new GadgetEntity { Name = "Anvil", StockLevel = 10 };
            db.Gadgets.Add(gadget);
            await db.SaveChangesAsync(Ct);
            return gadget.ID;
        });

        Assert.Empty(host.Dispatcher.Published);

        await host.InScopeAsync(async db =>
        {
            var gadget = await db.Gadgets.SingleAsync(x => x.ID == id, Ct);
            gadget.StockLevel = 1;
            db.SaveChanges();       // the synchronous save evaluates too

            Assert.True(gadget.HasActiveAttention);
        });

        Assert.Equal(id, Assert.Single(host.Dispatcher.Published).EntityId);
    }

    [Fact]
    public async Task The_summary_columns_are_written_when_the_caller_switched_change_detection_off()
    {
        // The caller marks its own change. Nothing would find the columns the evaluators set, so EF is told about them.
        await using var host = Host.Start();

        var id = await host.InScopeAsync(async db =>
        {
            var gadget = new GadgetEntity { Name = "Anvil", StockLevel = 10 };
            db.Gadgets.Add(gadget);
            await db.SaveChangesAsync(Ct);
            return gadget.ID;
        });

        await host.InScopeAsync(async db =>
        {
            var gadget = await db.Gadgets.SingleAsync(x => x.ID == id, Ct);
            db.ChangeTracker.AutoDetectChangesEnabled = false;
            gadget.StockLevel = 1;
            db.Entry(gadget).Property(x => x.StockLevel).IsModified = true;
            await db.SaveChangesAsync(Ct);
        });

        await host.InScopeAsync(async db =>
        {
            var stored = await db.Gadgets.AsNoTracking().SingleAsync(x => x.ID == id, Ct);
            Assert.True(stored.HasActiveAttention);
            Assert.Equal(1, stored.ActiveSignalCount);
            Assert.Equal(AttentionSeverity.Warning, stored.HighestSeverity);
        });
    }

    [Fact]
    public async Task A_repository_save_evaluates_every_attention_row_of_its_unit_of_work()
    {
        // The widget is not the gadget repository's own type. It used to be written unevaluated.
        await using var host = Host.Start();

        await host.InScopeAsync(async db =>
        {
            var gadget = new GadgetEntity { Name = "Anvil", StockLevel = 2 };
            var widget = new WidgetEntity { Name = "Sprocket", Flagged = true };
            db.Gadgets.Add(gadget);
            db.Widgets.Add(widget);

            await GadgetRepository(db).SaveChangesAsync();

            Assert.True(gadget.HasActiveAttention);
            Assert.True(widget.HasActiveAttention);
        });

        Assert.Equal(2, host.Dispatcher.Published.Count);
    }

    [Fact]
    public async Task A_host_can_keep_the_evaluators_to_repository_saves()
    {
        await using var host = Host.Start(services => services.Configure<AttentionOptions>(options => options.EvaluateOn = AttentionSaves.Repository));

        await host.InScopeAsync(async db =>
        {
            var onTheContext = new GadgetEntity { Name = "Anvil", StockLevel = 2 };
            db.Gadgets.Add(onTheContext);
            await db.SaveChangesAsync(Ct);

            Assert.False(onTheContext.HasActiveAttention);
            Assert.Empty(host.Dispatcher.Published);

            var throughARepository = new GadgetEntity { Name = "Hammer", StockLevel = 2 };
            db.Gadgets.Add(throughARepository);
            await GadgetRepository(db).SaveChangesAsync();

            Assert.True(throughARepository.HasActiveAttention);
            Assert.Equal(throughARepository.ID, Assert.Single(host.Dispatcher.Published).EntityId);
        });
    }

    [Fact]
    public async Task A_save_that_does_not_accept_its_changes_evaluates_nothing()
    {
        // Its caller may send it again, so the context adds nothing to it, as it reports nothing for it.
        await using var host = Host.Start();

        await host.InScopeAsync(async db =>
        {
            var gadget = new GadgetEntity { Name = "Anvil", StockLevel = 2 };
            db.Gadgets.Add(gadget);
            await db.SaveChangesAsync(acceptAllChangesOnSuccess: false, Ct);
            db.ChangeTracker.AcceptAllChanges();

            Assert.False(gadget.HasActiveAttention);
        });

        Assert.Empty(host.Dispatcher.Published);
    }

    // ── Clearing ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Clearing_a_signal_does_not_raise_it_again()
    {
        // The gadget's rule holds as long as stock is low. The save that acknowledges the signal changes the record's
        // row, and must not be read as a new reason to flag it.
        await using var host = Host.Start();

        await host.InScopeAsync(async db =>
        {
            var gadget = new GadgetEntity { Name = "Anvil", StockLevel = 2 };
            db.Gadgets.Add(gadget);
            await db.SaveChangesAsync(Ct);

            await db.ClearAttentionAsync<GadgetEntity>(gadget.ID);

            Assert.False(gadget.HasActiveAttention);
            Assert.Equal(0, gadget.ActiveSignalCount);
        });

        Assert.Single(host.Dispatcher.Published);

        // The next change of the record is asked about again, and the rule still holds.
        await host.InScopeAsync(async db =>
        {
            var gadget = await db.Gadgets.SingleAsync(Ct);
            gadget.Name = "Anvil (renamed)";
            await db.SaveChangesAsync(Ct);

            Assert.True(gadget.HasActiveAttention);
        });

        Assert.Equal(2, host.Dispatcher.Published.Count);
    }

    [Fact]
    public async Task A_save_that_changes_only_attentions_own_columns_asks_no_evaluator()
    {
        // A host acknowledging signals by hand, on the plain context: it marks the stored signals cleared and resets
        // the summary columns. Nothing about the record changed, so the rule, which still holds, is not asked.
        await using var host = Host.Start();

        await host.InScopeAsync(async db =>
        {
            var widget = new WidgetEntity { Name = "Sprocket", Flagged = true };
            var gadget = new GadgetEntity { Name = "Anvil", StockLevel = 2 };
            db.Widgets.Add(widget);
            db.Gadgets.Add(gadget);
            await db.SaveChangesAsync(Ct);

            foreach (var signal in await db.Set<AttentionSignalEntry>().Where(x => x.EntityId == widget.ID).ToListAsync(Ct))
                signal.ClearedAt = DateTimeOffset.UtcNow;

            widget.HasActiveAttention = false;
            widget.HighestSeverity = null;
            widget.ActiveSignalCount = 0;

            db.Entry(gadget).Property("AttentionSignalsJson").CurrentValue = "[]";
            gadget.HasActiveAttention = false;
            gadget.HighestSeverity = null;
            gadget.ActiveSignalCount = 0;

            await db.SaveChangesAsync(Ct);

            Assert.False(widget.HasActiveAttention);
            Assert.False(gadget.HasActiveAttention);
            Assert.Equal(2, host.Dispatcher.Published.Count);

            // A change of the record itself is asked about again. The widget's stored signal is cleared, in this same
            // context, so the rule raises a new one.
            widget.Name = "Sprocket (renamed)";
            await db.SaveChangesAsync(Ct);

            Assert.True(widget.HasActiveAttention);
            Assert.Equal(3, host.Dispatcher.Published.Count);
        });
    }

    [Fact]
    public async Task Signals_cleared_in_the_same_unit_of_work_count_as_cleared()
    {
        // The record changes and its stored signal is cleared by hand before one save. The evaluators read the signal
        // row this context tracks, cleared, and not the row the database still holds.
        await using var host = Host.Start();

        await host.InScopeAsync(async db =>
        {
            var widget = new WidgetEntity { Name = "Sprocket", Flagged = true };
            db.Widgets.Add(widget);
            await db.SaveChangesAsync(Ct);

            var stored = await db.Set<AttentionSignalEntry>().SingleAsync(x => x.EntityId == widget.ID, Ct);
            stored.ClearedAt = DateTimeOffset.UtcNow;
            widget.Name = "Sprocket (renamed)";
            await db.SaveChangesAsync(Ct);

            // Still flagged, so the rule raised again: one cleared signal, one new.
            Assert.True(widget.HasActiveAttention);
            Assert.Equal(1, widget.ActiveSignalCount);
            Assert.Equal(2, await db.Set<AttentionSignalEntry>().CountAsync(x => x.EntityId == widget.ID, Ct));
        });

        Assert.Equal(2, host.Dispatcher.Published.Count);
    }

    // ── Indexed signals ─────────────────────────────────────────────────────────

    [Fact]
    public async Task The_signals_of_a_new_indexed_row_are_written_with_its_key_and_published_after_both_writes()
    {
        await using var host = Host.Start();

        await host.InScopeAsync(async db =>
        {
            var widget = new WidgetEntity { Name = "Sprocket", Flagged = true };
            db.Widgets.Add(widget);

            Assert.Equal(1, await db.SaveChangesAsync(Ct));     // the caller's own row count

            var stored = Assert.Single(await db.Set<AttentionSignalEntry>().AsNoTracking().ToListAsync(Ct));
            Assert.Equal(widget.ID, stored.EntityId);
            Assert.Equal(nameof(WidgetEntity), stored.EntityType);
            Assert.Equal(widget.ID, Assert.Single(host.Dispatcher.Published).EntityId);
            Assert.Null(db.Database.CurrentTransaction);
        });
    }

    [Fact]
    public async Task The_synchronous_save_writes_the_signals_of_a_new_indexed_row_too()
    {
        await using var host = Host.Start();

        await host.InScopeAsync(async db =>
        {
            var widget = new WidgetEntity { Name = "Sprocket", Flagged = true };
            db.Widgets.Add(widget);

            Assert.Equal(1, db.SaveChanges());

            Assert.Equal(widget.ID, (await db.Set<AttentionSignalEntry>().AsNoTracking().SingleAsync(Ct)).EntityId);
            Assert.Equal(widget.ID, Assert.Single(host.Dispatcher.Published).EntityId);
            Assert.Null(db.Database.CurrentTransaction);
        });
    }

    [Fact]
    public async Task A_new_indexed_row_and_its_signals_commit_or_roll_back_together()
    {
        // The signal rows are a second write, after the row has its key. When that write fails, the row must not stay.
        await using var host = Host.Start();
        host.Commands.FailSave = 2;

        await host.InScopeAsync(async db =>
        {
            db.Widgets.Add(new WidgetEntity { Name = "Sprocket", Flagged = true });
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
        });

        await host.InScopeAsync(async db =>
        {
            Assert.Equal(0, await db.Widgets.CountAsync(Ct));
            Assert.Equal(0, await db.Set<AttentionSignalEntry>().CountAsync(Ct));
        });

        Assert.Empty(host.Dispatcher.Published);
    }

    [Fact]
    public async Task A_save_of_many_indexed_rows_reads_their_stored_signals_with_one_query()
    {
        const int widgets = 40;
        await using var host = Host.Start();

        await host.InScopeAsync(async db =>
        {
            for (var i = 0; i < widgets; i++)
                db.Widgets.Add(new WidgetEntity { Name = "Sprocket " + i });
            await db.SaveChangesAsync(Ct);
        });

        await host.InScopeAsync(async db =>
        {
            var all = await db.Widgets.ToListAsync(Ct);
            host.Commands.SignalQueries = 0;

            foreach (var widget in all)
                widget.Flagged = true;
            await db.SaveChangesAsync(Ct);

            Assert.Equal(1, host.Commands.SignalQueries);
            Assert.All(all, widget => Assert.True(widget.HasActiveAttention));
        });

        Assert.Equal(widgets, host.Dispatcher.Published.Count);
        Assert.Equal(widgets, await host.InScopeAsync(db => db.Set<AttentionSignalEntry>().CountAsync(Ct)));

        // Saved again while still flagged: the stored signals are read, and they suppress the same signal.
        await host.InScopeAsync(async db =>
        {
            foreach (var widget in await db.Widgets.ToListAsync(Ct))
                widget.Name += " (renamed)";
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(widgets, host.Dispatcher.Published.Count);
        Assert.Equal(widgets, await host.InScopeAsync(db => db.Set<AttentionSignalEntry>().CountAsync(Ct)));
    }

    // ── After the commit ────────────────────────────────────────────────────────

    [Fact]
    public async Task Events_wait_for_the_callers_commit_and_a_rollback_drops_them()
    {
        await using var host = Host.Start();

        await host.InScopeAsync(async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);

            // The widget is new and indexed, so its signal is written second, inside the caller's transaction too.
            db.Gadgets.Add(new GadgetEntity { Name = "Kept", StockLevel = 2 });
            db.Widgets.Add(new WidgetEntity { Name = "Kept", Flagged = true });
            await db.SaveChangesAsync(Ct);
            host.Log.Add("saved");

            await transaction.CommitAsync(Ct);
        });

        await host.InScopeAsync(async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);

            db.Gadgets.Add(new GadgetEntity { Name = "Dropped", StockLevel = 2 });
            db.Widgets.Add(new WidgetEntity { Name = "Dropped", Flagged = true });
            await db.SaveChangesAsync(Ct);
            host.Log.Add("saved");

            await transaction.RollbackAsync(Ct);
        });

        Assert.Equal(["saved", "published LowStock of GadgetEntity 1", "published Flagged of WidgetEntity 1", "saved"], host.Log);
        Assert.Equal(1, await host.InScopeAsync(db => db.Set<AttentionSignalEntry>().CountAsync(Ct)));
    }

    // What a test tells the gadgets' after-save handler to do.
    public sealed class GadgetAfterSaveSwitch
    {
        public bool Fail { get; set; }
    }

    public sealed class GadgetAfterSave(List<string> log, GadgetAfterSaveSwitch what) : IAfterSaveHandler<GadgetEntity>
    {
        public ValueTask AfterSaveAsync(SavedRows<GadgetEntity> rows, CancellationToken cancellationToken)
        {
            log.Add("after-save handler");

            if (what.Fail)
                throw new InvalidOperationException("The handler failed.");

            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task Events_of_a_save_with_an_after_save_handler_wait_for_the_transaction_the_context_opens_for_it()
    {
        // An after-save handler makes the save run in a transaction of the context's own, and the handler runs inside
        // it. The events of the new signals wait for that transaction's commit, and a failed save publishes nothing.
        var what = new GadgetAfterSaveSwitch();
        await using var host = Host.Start(services => services.AddSingleton(what).AddSaveHandler<GadgetAfterSave>());

        await host.InScopeAsync(async db =>
        {
            db.Gadgets.Add(new GadgetEntity { Name = "Kept", StockLevel = 2 });
            await GadgetRepository(db).SaveChangesAsync();
        });

        Assert.Equal(["after-save handler", "published LowStock of GadgetEntity 1"], host.Log);

        host.Log.Clear();
        what.Fail = true;

        await host.InScopeAsync(async db =>
        {
            db.Gadgets.Add(new GadgetEntity { Name = "Dropped", StockLevel = 2 });
            await Assert.ThrowsAsync<InvalidOperationException>(() => GadgetRepository(db).SaveChangesAsync());
        });

        Assert.Equal(["after-save handler"], host.Log);
        Assert.Equal(1, await host.InScopeAsync(db => db.Gadgets.CountAsync(Ct)));
    }

    [Fact]
    public async Task A_save_that_raises_a_signal_is_refused_inside_a_transaction_the_context_cannot_follow()
    {
        // The event of a new signal waits for the commit, and the context is never told when a transaction it joined
        // with UseTransaction commits. The save is refused while nothing is written.
        await using var host = Host.Start();

        await host.InScopeAsync(async db =>
        {
            await db.Database.OpenConnectionAsync(Ct);
            await using var owner = await db.Database.GetDbConnection().BeginTransactionAsync(Ct);
            await db.Database.UseTransactionAsync(owner, Ct);

            db.Gadgets.Add(new GadgetEntity { Name = "Flagged", StockLevel = 2 });
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
            Assert.Contains("cannot save rows that raise an attention signal", refused.Message);
            Assert.Contains("UseTransaction", refused.Message);

            // A record that raises nothing has nothing that waits, and is saved in that transaction.
            db.ChangeTracker.Clear();
            db.Gadgets.Add(new GadgetEntity { Name = "Fine", StockLevel = 50 });
            await db.SaveChangesAsync(Ct);

            await owner.CommitAsync(Ct);
            await db.Database.UseTransactionAsync(null, Ct);
        });

        Assert.Empty(host.Dispatcher.Published);
        Assert.Equal(["Fine"], await host.InScopeAsync(db => db.Gadgets.Select(x => x.Name).ToListAsync(Ct)));
    }

    // ── After the before-save handlers ──────────────────────────────────────────

    /// <summary>When a gadget is added, adds a flagged widget for it. The widgets' own handler runs in the next round.</summary>
    public sealed class WidgetForAGadget : IBeforeSaveHandler<GadgetEntity>
    {
        public ValueTask BeforeSaveAsync(PendingRows<GadgetEntity> rows, CancellationToken cancellationToken)
        {
            foreach (var row in rows)
                if (row.Change == RowChange.Added)
                    rows.Db.Add(new WidgetEntity { Name = "For " + row.Entity.Name, Flagged = true });

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>In the round after: takes the stock of every gadget of the save down, a row an earlier round offered.</summary>
    public sealed class WidgetsUseStock : IBeforeSaveHandler<WidgetEntity>
    {
        public ValueTask BeforeSaveAsync(PendingRows<WidgetEntity> rows, CancellationToken cancellationToken)
        {
            foreach (var gadget in rows.Db.ChangeTracker.Entries<GadgetEntity>())
                gadget.Entity.StockLevel = 1;

            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task The_evaluators_run_once_after_the_handlers_and_see_what_every_round_left()
    {
        await using var host = Host.Start(services => services.AddSaveHandler<WidgetForAGadget>().AddSaveHandler<WidgetsUseStock>());

        await host.InScopeAsync(async db =>
        {
            // Saved with enough stock: on its own it raises nothing.
            var gadget = new GadgetEntity { Name = "Anvil", StockLevel = 10 };
            db.Gadgets.Add(gadget);
            await db.SaveChangesAsync(Ct);

            // The second round took the stock down, and the signal is about what was written.
            Assert.Equal(1, gadget.StockLevel);
            Assert.True(gadget.HasActiveAttention);

            // The widget a handler added is stamped and evaluated like a row the caller added.
            var widget = db.Widgets.Local.Single();
            Assert.NotEqual(default, widget.CreateDate);
            Assert.True(widget.HasActiveAttention);
        });

        Assert.Equal(
            ["Flagged of WidgetEntity: Widget was flagged", "LowStock of GadgetEntity: Stock is down to 1"],
            host.Dispatcher.Published.Select(x => $"{x.Signal.Category} of {x.EntityType}: {x.Signal.Reason}").Order());
    }

    /// <summary>Notes the rows it is offered before the save and once they are committed, for every type of the model.</summary>
    public sealed class EveryRow<TEntity>(List<string> log) : IBeforeSaveHandler<TEntity>, IAfterCommitHandler<TEntity> where TEntity : class
    {
        public ValueTask BeforeSaveAsync(PendingRows<TEntity> rows, CancellationToken cancellationToken)
        {
            log.Add($"before save: {rows.Count} {typeof(TEntity).Name}");
            return ValueTask.CompletedTask;
        }

        public ValueTask AfterCommitAsync(SavedRows<TEntity> rows, CancellationToken cancellationToken)
        {
            log.Add($"after commit: {rows.Count} {typeof(TEntity).Name}");
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task The_signal_rows_the_evaluators_add_reach_no_before_save_handler_and_are_rows_like_any_other_once_written()
    {
        // A handler for every type of the model, the signals table among them.
        await using var host = Host.Start(services => services.AddSaveHandler(typeof(EveryRow<>)));

        await host.InScopeAsync(async db =>
        {
            db.Widgets.Add(new WidgetEntity { Name = "Sprocket" });
            await db.SaveChangesAsync(Ct);
        });

        host.Log.Clear();

        // A widget keeps its signals as rows of a table. Flagging it makes the evaluators add one to this save.
        await host.InScopeAsync(async db =>
        {
            (await db.Widgets.SingleAsync(Ct)).Flagged = true;
            await db.SaveChangesAsync(Ct);

            Assert.Equal(1, await db.Set<AttentionSignalEntry>().CountAsync(Ct));
        });

        // The signal row is the framework's bookkeeping: it is added after the rounds, so no before-save handler is
        // offered it. Once written it is a row like any other.
        Assert.Contains("before save: 1 WidgetEntity", host.Log);
        Assert.DoesNotContain(host.Log, line => line.StartsWith("before save") && line.Contains(nameof(AttentionSignalEntry)));
        Assert.Contains("after commit: 1 WidgetEntity", host.Log);
        Assert.Contains($"after commit: 1 {nameof(AttentionSignalEntry)}", host.Log);
    }

    [Fact]
    public async Task Under_SuppressAfterCommit_a_record_is_still_flagged_and_nobody_is_told()
    {
        // Evaluating keeps the data right. The event of a new signal is what waits for the commit and tells others.
        await using var host = Host.Start();

        await host.InScopeAsync(async db =>
        {
            var gadget = new GadgetEntity { Name = "Anvil", StockLevel = 2 };
            db.Gadgets.Add(gadget);

            using (db.SuppressAfterCommit())
                await db.SaveChangesAsync(Ct);

            Assert.True(gadget.HasActiveAttention);
        });

        Assert.Empty(host.Dispatcher.Published);
        Assert.True(await host.InScopeAsync(db => db.Gadgets.Select(x => x.HasActiveAttention).SingleAsync(Ct)));
    }

    // ── A save made from inside a repository's save ─────────────────────────────

    /// <summary>After a gadget is saved, saves a flagged widget in a save of its own, inside the save it follows.</summary>
    public sealed class WidgetAfterAGadget : IAfterSaveHandler<GadgetEntity>
    {
        public async ValueTask AfterSaveAsync(SavedRows<GadgetEntity> rows, CancellationToken cancellationToken)
        {
            rows.Db.Add(new WidgetEntity { Name = "After " + rows[0].Entity.Name, Flagged = true });
            await rows.Db.SaveChangesAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task For_a_host_that_keeps_the_evaluators_to_repository_saves_a_save_made_from_inside_a_repository_save_is_one()
    {
        await using var host = Host.Start(services => services
            .Configure<AttentionOptions>(options => options.EvaluateOn = AttentionSaves.Repository)
            .AddSaveHandler<WidgetAfterAGadget>());

        await host.InScopeAsync(async db =>
        {
            // On the plain context nothing is evaluated: not the gadget, and not the widget the handler saves after it.
            db.Gadgets.Add(new GadgetEntity { Name = "Anvil", StockLevel = 50 });
            await db.SaveChangesAsync(Ct);

            // Through a repository, the save the handler makes from inside it is evaluated as the repository's is.
            db.Gadgets.Add(new GadgetEntity { Name = "Hammer", StockLevel = 50 });
            await GadgetRepository(db).SaveChangesAsync();
        });

        var widgets = await host.InScopeAsync(db => db.Widgets.AsNoTracking().OrderBy(x => x.ID).ToListAsync(Ct));

        Assert.Equal(["After Anvil: False", "After Hammer: True"], widgets.Select(x => $"{x.Name}: {x.HasActiveAttention}"));

        Assert.Equal("WidgetEntity", Assert.Single(host.Dispatcher.Published).EntityType);
    }
}
