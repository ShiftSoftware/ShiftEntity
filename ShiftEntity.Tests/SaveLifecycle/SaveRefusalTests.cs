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
/// A save that cannot keep its promises is refused, before it writes wherever the context can know by then: under an
/// execution strategy that retries, with transactions switched off, and when its changes are not accepted. And what
/// the stages do on a provider that has no transactions at all.
/// </summary>
public class SaveRefusalTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Script<Order> AfterSaveAndAfterCommit => new() { AfterSave = Nothing, AfterCommit = Nothing };

    // ── An execution strategy that retries ──────────────────────────────────────

    // Retries a timeout, as EnableRetryOnFailure does a transient SQL error.
    public sealed class RetryingStrategy(ExecutionStrategyDependencies dependencies)
        : ExecutionStrategy(dependencies, 3, TimeSpan.FromMilliseconds(5))
    {
        protected override bool ShouldRetryOn(Exception exception) => exception is TimeoutException;
    }

    [Fact]
    public async Task Under_a_retrying_strategy_a_save_that_needs_the_contexts_own_transaction_is_refused_before_it_writes()
    {
        await using var host = SavePipelineHost.Start(
            services => services.AddScript(AfterSaveAndAfterCommit).AddScript(new Script<Memo> { AfterCommit = Nothing }),
            sqlite: sqlite => sqlite.ExecutionStrategy(dependencies => new RetryingStrategy(dependencies)));

        await host.InScopeAsync(async db =>
        {
            var order = new Order { Name = "a" };
            db.Add(order);

            // EF accepts the rows before the context's transaction commits, so a retried attempt would find nothing
            // to save and lose them without an error.
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
            Assert.Contains("cannot save Order rows, which have an after-save or a before-commit handler", refused.Message);
            Assert.Contains("retries on failure (RetryingStrategy)", refused.Message);
            Assert.Contains("EnableRetryOnFailure", refused.Message);
            Assert.Empty(host.Sql.Lines);
            Assert.Equal(EntityState.Added, db.Entry(order).State);

            // Also when the strategy runs the save as part of a larger operation.
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                db.Database.CreateExecutionStrategy().ExecuteAsync(() => db.SaveChangesAsync(Ct)));
            Assert.Empty(host.Sql.Lines);

            // A save that needs no transaction is retried by EF as always.
            db.Entry(order).State = EntityState.Detached;
            db.Add(new Memo { Name = "only told about after the commit" });
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(["after commit of Memo: Added Memo 'only told about after the commit'"], host.Log.Lines);
    }

    [Fact]
    public async Task Under_a_retrying_strategy_the_apps_own_transaction_inside_Execute_works_and_runs_the_stages()
    {
        await using var host = SavePipelineHost.Start(
            services => services.AddScript(AfterSaveAndAfterCommit),
            sqlite: sqlite => sqlite.ExecutionStrategy(dependencies => new RetryingStrategy(dependencies)));

        await using var scope = host.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Db>();

        // EF's pattern: the delegate begins the transaction and redoes its own changes on each attempt.
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);

            db.Add(new Order { Name = "a" });
            await db.SaveChangesAsync(Ct);
            await transaction.CommitAsync(Ct);
        });

        Assert.Equal(["after save of Order: Added Order 'a'", "after commit of Order: Added Order 'a'"], host.Log.Lines);
        Assert.Equal(1, await db.Orders.CountAsync(Ct));
    }

    // ── AutoTransactionBehavior.Never ───────────────────────────────────────────

    [Fact]
    public async Task With_AutoTransactionBehavior_Never_a_save_that_needs_the_contexts_own_transaction_is_refused_before_it_writes()
    {
        await using var host = SavePipelineHost.Start(services => services.AddScript(AfterSaveAndAfterCommit));

        await host.InScopeAsync(async db =>
        {
            db.Database.AutoTransactionBehavior = AutoTransactionBehavior.Never;
            db.Add(new Order { Name = "a" });

            // The app has said: no transactions.
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
            Assert.Contains("cannot save Order rows, which have an after-save or a before-commit handler, with AutoTransactionBehavior.Never", refused.Message);
            Assert.Contains("Begin a transaction through Database before the save", refused.Message);
            Assert.Empty(host.Sql.Lines);

            // A transaction of the app's own is one the save can run in.
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            await db.SaveChangesAsync(Ct);
            await transaction.CommitAsync(Ct);
        });

        Assert.Equal(["after save of Order: Added Order 'a'", "after commit of Order: Added Order 'a'"], host.Log.Lines);
    }

    // ── A save that does not accept its changes ─────────────────────────────────

    [Fact]
    public async Task A_save_that_does_not_accept_its_changes_is_refused_when_a_handler_has_its_rows_and_is_EFs_own_save_otherwise()
    {
        await using var host = SavePipelineHost.Start(services => services.AddScript(new Script<Order> { AfterCommit = Nothing }));

        await host.InScopeAsync(async db =>
        {
            var order = new Order { Name = "a" };
            db.Add(order);

            // After such a save the record does not hold its new key, and EF raises no event for the rows it wrote:
            // no stage after the write could work on them.
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(acceptAllChangesOnSuccess: false, Ct));
            Assert.Contains("cannot save Order rows with acceptAllChangesOnSuccess: false", refused.Message);
            Assert.Contains("SuppressSaveHandlers", refused.Message);
            Assert.Empty(host.Sql.Lines);
            Assert.Throws<InvalidOperationException>(() => db.SaveChanges(acceptAllChangesOnSuccess: false));

            // A memo has no handler: the save behaves as in EF.
            db.Entry(order).State = EntityState.Detached;
            var memo = new Memo { Name = "b" };
            db.Add(memo);
            Assert.Equal(1, await db.SaveChangesAsync(acceptAllChangesOnSuccess: false, Ct));
            Assert.Equal(EntityState.Added, db.Entry(memo).State);
            db.ChangeTracker.AcceptAllChanges();

            // With the handlers of that stage switched off for the save, so does the order.
            db.Add(order);
            using (db.SuppressSaveHandlers(SaveStages.AfterCommit))
                await db.SaveChangesAsync(acceptAllChangesOnSuccess: false, Ct);
            db.ChangeTracker.AcceptAllChanges();
        });

        Assert.Empty(host.Log.Lines);
        Assert.Equal((1, 1), await host.InScopeAsync(async db => (await db.Orders.CountAsync(Ct), await db.Memos.CountAsync(Ct))));
    }

    // ── A provider without transactions ─────────────────────────────────────────

    private static ServiceProvider InMemoryHost(Action<IServiceCollection> services, bool ignoreTransactions = false)
    {
        var name = "lifecycle-" + Guid.NewGuid().ToString("N");
        var collection = new ServiceCollection();
        collection.AddSingleton<SaveLog>();
        collection.AddDbContext<Db>(options =>
        {
            options.UseInMemoryDatabase(name);

            if (ignoreTransactions)
                options.ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning));
        });
        services(collection);
        return collection.BuildServiceProvider(validateScopes: true);
    }

    [Fact]
    public async Task On_a_provider_without_transactions_every_stage_runs_in_order_and_a_nested_save_shares_the_commit()
    {
        await using var provider = InMemoryHost(services => services
            .AddScript(new Script<Order>
            {
                AfterSave = async (rows, ct) =>
                {
                    rows.Db.Add(new Memo { Name = "memo of " + rows[0].Entity.Name });
                    await rows.Db.SaveChangesAsync(ct);
                },
                BeforeCommit = Nothing,
                AfterCommit = Nothing,
            })
            .AddSaveHandler<StageLog<Memo>>());

        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Db>();

        db.Add(new Order { Name = "a" });
        await db.SaveChangesAsync(Ct);

        // No transaction is opened: the provider would refuse one.
        Assert.Null(db.Database.CurrentTransaction);
        Assert.Equal(
        [
            "after save of Order: Added Order 'a'",
            "after save of Memo: Added Memo 'memo of a'",
            "before commit of Order: Added Order 'a'",
            "before commit of Memo: Added Memo 'memo of a'",
            "after commit of Order: Added Order 'a'",
            "after commit of Memo: Added Memo 'memo of a'",
        ], provider.GetRequiredService<SaveLog>().Lines);
    }

    [Fact]
    public async Task On_a_provider_without_transactions_a_failure_before_the_commit_fails_the_save_but_cannot_undo_the_write()
    {
        await using var provider = InMemoryHost(services => services.AddScript(new Script<Order>
        {
            AfterSave = (_, _) => throw new InvalidOperationException("The handler failed."),
            AfterCommit = Nothing,
        }));

        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Db>();

        db.Add(new Order { Name = "a" });
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
        Assert.Equal("The handler failed.", failure.Message);

        // Nothing was rolled back, so the context holds no rows of an undone save, and nothing ran after the commit.
        Assert.Equal(1, await db.Orders.AsNoTracking().CountAsync(Ct));
        Assert.Equal(0, await db.SaveChangesAsync(Ct));
        Assert.Equal(["after save of Order: Added Order 'a'"], provider.GetRequiredService<SaveLog>().Lines);
    }

    [Fact]
    public async Task On_a_provider_that_ignores_transactions_the_one_the_app_begins_is_still_followed()
    {
        await using var provider = InMemoryHost(
            services => services.AddScript(new Script<Order> { AfterSave = Nothing, BeforeCommit = Nothing, AfterCommit = Nothing }),
            ignoreTransactions: true);

        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Db>();
        var log = provider.GetRequiredService<SaveLog>();

        await using (var transaction = await db.Database.BeginTransactionAsync(Ct))
        {
            Assert.Same(transaction, db.Database.CurrentTransaction);

            db.Add(new Order { Name = "a" });
            await db.SaveChangesAsync(Ct);
            db.Add(new Order { Name = "b" });
            await db.SaveChangesAsync(Ct);

            log.Add("commit");
            await transaction.CommitAsync(Ct);
        }

        Assert.Null(db.Database.CurrentTransaction);
        Assert.Equal(
        [
            "after save of Order: Added Order 'a'",
            "after save of Order: Added Order 'b'",
            "commit",
            "before commit of Order: Added Order 'a', Added Order 'b'",
            "after commit of Order: Added Order 'a', Added Order 'b'",
        ], log.Lines);
    }
}
