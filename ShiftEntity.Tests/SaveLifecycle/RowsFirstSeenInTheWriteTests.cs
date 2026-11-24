using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.Tests.SaveHandlers;
using Xunit;
using static ShiftSoftware.ShiftEntity.Tests.SaveHandlers.SavePipelineModel;
using static ShiftSoftware.ShiftEntity.Tests.SaveLifecycle.ScriptedHandlers;

namespace ShiftSoftware.ShiftEntity.Tests.SaveLifecycle;

/// <summary>
/// A row that first appears during EF's own write: an interceptor of the app's adds it as the save starts. The context
/// had decided by then whether the save needs a transaction, so the row's handlers that must run inside one run only
/// when a transaction happens to be open. What runs after the commit runs for it like for any other row.
/// </summary>
public class RowsFirstSeenInTheWriteTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Adds a memo for each new order as EF's save starts.
    public sealed class AddsAMemoForEachNewOrder : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var db = (Db)eventData.Context!;

            foreach (var order in db.ChangeTracker.Entries<Order>().Where(entry => entry.State == EntityState.Added).ToList())
                db.Add(new Memo { Name = "memo of " + order.Entity.Name });

            return ValueTask.FromResult(result);
        }
    }

    private static SavePipelineHost Host(Script<Memo> memos) => SavePipelineHost.Start(
        services => services.AddScript(memos),
        context => context.AddInterceptors(new AddsAMemoForEachNewOrder()));

    [Fact]
    public async Task Its_after_commit_handler_runs_as_for_any_other_row()
    {
        await using var host = Host(new Script<Memo> { AfterCommit = Nothing });

        await host.InScopeAsync(async db =>
        {
            db.Add(new Order { Name = "a" });
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(["after commit of Memo: Added Memo 'memo of a'"], host.Log.Lines);
    }

    [Fact]
    public async Task With_an_after_save_handler_and_no_transaction_the_save_reports_the_failure_and_names_the_type()
    {
        await using var host = Host(new Script<Memo> { AfterSave = Nothing, AfterCommit = Nothing });

        await host.InScopeAsync(async db =>
        {
            db.Add(new Order { Name = "a" });

            // The order has no handler, so the save needed no transaction, and its write was the commit. The memo's
            // after-save handler must not run outside a transaction, and nothing can be undone any more.
            var failed = await Assert.ThrowsAsync<AfterCommitException>(() => db.SaveChangesAsync(Ct));

            var failure = Assert.IsType<InvalidOperationException>(Assert.Single(failed.Failures));
            Assert.Contains("A Memo row was saved by Db without its handlers that run inside the save's transaction", failure.Message);
            Assert.Contains("OnAfterSave`1 (after save, Memo)", failure.Message);
            Assert.Contains("first appeared during EF's write", failure.Message);
        });

        // The rest still ran, and both rows are saved.
        Assert.Equal(["after commit of Memo: Added Memo 'memo of a'"], host.Log.Lines);
        Assert.Equal((1, 1), await host.InScopeAsync(async db => (await db.Orders.CountAsync(Ct), await db.Memos.CountAsync(Ct))));
    }

    [Fact]
    public async Task Inside_a_transaction_its_after_save_handler_runs()
    {
        await using var host = Host(new Script<Memo> { AfterSave = Nothing, BeforeCommit = Nothing, AfterCommit = Nothing });

        await host.InScopeAsync(async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);

            db.Add(new Order { Name = "a" });
            await db.SaveChangesAsync(Ct);

            await transaction.CommitAsync(Ct);
        });

        Assert.Equal(
        [
            "after save of Memo: Added Memo 'memo of a'",
            "before commit of Memo: Added Memo 'memo of a'",
            "after commit of Memo: Added Memo 'memo of a'",
        ], host.Log.Lines);
    }
}
