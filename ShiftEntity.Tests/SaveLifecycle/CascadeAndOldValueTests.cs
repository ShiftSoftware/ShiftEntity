using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.Tests.SaveHandlers;
using Xunit;
using static ShiftSoftware.ShiftEntity.Tests.SaveHandlers.SavePipelineModel;
using static ShiftSoftware.ShiftEntity.Tests.SaveLifecycle.ScriptedHandlers;

namespace ShiftSoftware.ShiftEntity.Tests.SaveLifecycle;

/// <summary>
/// Rows that EF deletes by cascade, which the before-save handlers must get although EF would only find them during
/// its write. And the old values a handler after the write is given for a row that a before-save handler changed.
/// </summary>
public class CascadeAndOldValueTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task SeedAnOrderWithTwoLinesAsync(SavePipelineHost host)
    {
        await using var db = host.Bare();

        db.Add(new Order { Name = "a", Status = "new", Lines = [new OrderLine { Note = "first" }, new OrderLine { Note = "second" }] });
        await db.SaveChangesAsync(Ct);
    }

    private static Script<OrderLine> LinesInEveryStage() => new()
    {
        BeforeSave = (rows, _) => Task.CompletedTask,
        AfterSave = Nothing,
        BeforeCommit = Nothing,
        AfterCommit = Nothing,
    };

    private const string TwoDeletedLines = "Deleted OrderLine '', Deleted OrderLine ''";

    // ── Cascades ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(CascadeTiming.Immediate)]
    [InlineData(CascadeTiming.OnSaveChanges)]
    public async Task The_lines_of_a_deleted_order_reach_every_stage_as_deleted_rows_whenever_EF_would_cascade(CascadeTiming timing)
    {
        await using var host = SavePipelineHost.Start(services => services.AddScript(LinesInEveryStage()));
        await SeedAnOrderWithTwoLinesAsync(host);

        await host.InScopeAsync(async db =>
        {
            // With OnSaveChanges EF cascades only inside its own save, after the before-save stage. The context
            // resolves the cascade before the first round, so that the handlers of the lines get them.
            db.ChangeTracker.CascadeDeleteTiming = timing;
            db.ChangeTracker.DeleteOrphansTiming = timing;

            var order = await db.Orders.Include(x => x.Lines).SingleAsync(Ct);
            db.Remove(order);
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(
        [
            $"before save of OrderLine: {TwoDeletedLines}",
            $"after save of OrderLine: {TwoDeletedLines}",
            $"before commit of OrderLine: {TwoDeletedLines}",
            $"after commit of OrderLine: {TwoDeletedLines}",
        ], host.Log.Lines);

        Assert.Equal(0, await host.InScopeAsync(db => db.Lines.CountAsync(Ct)));
    }

    [Theory]
    [InlineData(CascadeTiming.Immediate)]
    [InlineData(CascadeTiming.OnSaveChanges)]
    public async Task A_line_taken_out_of_its_order_reaches_the_before_save_handlers_as_a_deleted_row(CascadeTiming timing)
    {
        await using var host = SavePipelineHost.Start(services => services.AddScript(new Script<OrderLine> { BeforeSave = (_, _) => Task.CompletedTask }));
        await SeedAnOrderWithTwoLinesAsync(host);

        await host.InScopeAsync(async db =>
        {
            db.ChangeTracker.CascadeDeleteTiming = timing;
            db.ChangeTracker.DeleteOrphansTiming = timing;

            // A line cannot be without its order, so EF deletes the orphan.
            var order = await db.Orders.Include(x => x.Lines).SingleAsync(Ct);
            order.Lines.RemoveAt(0);
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(["before save of OrderLine: Deleted OrderLine ''"], host.Log.Lines);
        Assert.Equal(1, await host.InScopeAsync(db => db.Lines.CountAsync(Ct)));
    }

    [Fact]
    public async Task With_a_cascade_timing_of_Never_the_context_cascades_nothing_and_EFs_save_throws_as_it_does_without_the_framework()
    {
        await using var host = SavePipelineHost.Start(services => services.AddScript(LinesInEveryStage()));
        await SeedAnOrderWithTwoLinesAsync(host);

        static async Task<Exception> SaveAsync(Db db)
        {
            // The app has said that it resolves cascades itself. It does not here.
            db.ChangeTracker.CascadeDeleteTiming = CascadeTiming.Never;
            db.ChangeTracker.DeleteOrphansTiming = CascadeTiming.Never;

            var order = await db.Orders.Include(x => x.Lines).SingleAsync(Ct);
            db.Remove(order);

            return await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync(Ct));
        }

        // A context without the host's services has no handlers: this is what EF does by itself.
        await using var bare = host.Bare();
        var plain = await SaveAsync(bare);

        var withHandlers = await host.InScopeAsync(SaveAsync);

        Assert.Equal(plain.GetType(), withHandlers.GetType());
        Assert.Equal(plain.Message, withHandlers.Message);

        // No line was deleted by the context, so no handler was given one, and nothing was written.
        Assert.Empty(host.Log.Lines);
        Assert.Equal(2, await host.InScopeAsync(db => db.Lines.CountAsync(Ct)));
    }

    // ── Old values ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_old_values_of_a_row_are_the_ones_from_before_the_save_also_when_a_before_save_handler_changed_it()
    {
        // The handler changes every order the context tracks: the one the caller changed, and one it only loaded.
        await using var host = SavePipelineHost.Start(services => services
            .AddScript(new Script<Order>
            {
                BeforeSave = (rows, _) =>
                {
                    foreach (var order in rows.Db.ChangeTracker.Entries<Order>().ToList())
                        order.Entity.Status = "set by a handler";

                    return Task.CompletedTask;
                },
            })
            .AddSaveHandler<OrderBeforeAndAfter>());

        await host.InScopeAsync(async db =>
        {
            db.AddRange(new Order { Name = "changed by the caller", Status = "new" }, new Order { Name = "only loaded", Status = "new" });

            using (db.SuppressAllSaveHandlers())
                await db.SaveChangesAsync(Ct);
        });

        await host.InScopeAsync(async db =>
        {
            var orders = await db.Orders.OrderBy(x => x.ID).ToListAsync(Ct);
            orders[0].Name = "renamed";
            await db.SaveChangesAsync(Ct);
        });

        // The handler after the write compares with what the database held, not with what the caller or an earlier
        // handler made of the record during the save.
        Assert.Equal(
        [
            "before save of Order: Modified Order 'renamed'",
            "before save of Order: Modified Order 'only loaded'",
            "Modified: 'changed by the caller'/'new' -> 'renamed'/'set by a handler'",
            "Modified: 'only loaded'/'new' -> 'only loaded'/'set by a handler'",
        ], host.Log.Lines);
    }
}
