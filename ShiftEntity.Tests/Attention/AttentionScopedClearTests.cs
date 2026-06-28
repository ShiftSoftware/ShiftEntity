using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.Core.Attention;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.EFCore.Attention;
using ShiftSoftware.ShiftEntity.EFCore.Entities;
using ShiftSoftware.ShiftEntity.Tests.Attention.Scenario;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Attention;

/// <summary>
/// Scoped &amp; per-signal clearing. One entity carries two signals in different clear scopes
/// (a default-scope signal from its self-evaluator plus a "Review"-scope signal from a registered
/// evaluator). <see cref="AttentionClearFilter"/> selects which to clear; the pipeline clears only
/// the matches and <em>recomputes</em> the summary columns from the remainder. Exercised through
/// the public in-process <c>ClearAttentionAsync</c> façade, on both the JSON-shadow path
/// (<see cref="GadgetEntity"/>) and the indexed path (<see cref="WidgetEntity"/>).
/// </summary>
public class AttentionScopedClearTests
{
    private static ShiftRepository<AttentionTestDbContext, GadgetEntity, GadgetDTO, GadgetDTO> GadgetRepository(
        AttentionTestDbContext db) => new(db, o => o.UseMapper(new ThrowingGadgetMapper()));

    private static ShiftRepository<AttentionTestDbContext, WidgetEntity, WidgetDTO, WidgetDTO> WidgetRepository(
        AttentionTestDbContext db) => new(db, o => o.UseMapper(new ThrowingWidgetMapper()));

    // A low-stock gadget fires both evaluators: LowStock (default scope, Warning) + NeedsReview
    // (Review scope, Info). Returns the tracked gadget so tests assert its summary columns directly.
    private static async Task<GadgetEntity> SaveTwoSignalGadgetAsync(AttentionTestDbContext db)
    {
        var gadget = new GadgetEntity { Name = "Anvil", StockLevel = 2 };
        db.Gadgets.Add(gadget);
        await GadgetRepository(db).SaveChangesAsync();

        // Sanity: two distinct-key signals were raised.
        Assert.Equal(2, gadget.ActiveSignalCount);
        Assert.Equal(AttentionSeverity.Warning, gadget.HighestSeverity);
        return gadget;
    }

    private static Task<AttentionApp> StartGadgetAppAsync() =>
        AttentionApp.StartAsync(s => s.AddAttentionEvaluator<GadgetEntity, GadgetReviewEvaluator>());

    [Fact]
    public async Task ByScope_ClearsOnlyThatScope_AndRecomputesSummary()
    {
        await using var app = await StartGadgetAppAsync();
        using var scope = app.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AttentionTestDbContext>();

        var gadget = await SaveTwoSignalGadgetAsync(db);

        await db.ClearAttentionAsync<GadgetEntity>(
            gadget.ID, AttentionClearFilter.ByScope(GadgetReviewEvaluator.ReviewScope));

        // Review-scope signal gone; default-scope LowStock survives; summary recomputed from it.
        Assert.True(gadget.HasActiveAttention);
        Assert.Equal(1, gadget.ActiveSignalCount);
        Assert.Equal(AttentionSeverity.Warning, gadget.HighestSeverity);
    }

    [Fact]
    public async Task DefaultScope_ClearsOnly_DefaultScope_LeavingNamedScopeSignals()
    {
        await using var app = await StartGadgetAppAsync();
        using var scope = app.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AttentionTestDbContext>();

        var gadget = await SaveTwoSignalGadgetAsync(db);

        // This is what ClearAttentionOnOpen posts: only the default (unscoped) bucket.
        await db.ClearAttentionAsync<GadgetEntity>(gadget.ID, AttentionClearFilter.DefaultScope);

        // LowStock (default) gone; the "Review" signal remains; severity recomputed to its Info.
        Assert.True(gadget.HasActiveAttention);
        Assert.Equal(1, gadget.ActiveSignalCount);
        Assert.Equal(AttentionSeverity.Info, gadget.HighestSeverity);
    }

    [Fact]
    public async Task Signal_ClearsExactlyOneSignal_ByDedupKey()
    {
        await using var app = await StartGadgetAppAsync();
        using var scope = app.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AttentionTestDbContext>();

        var gadget = await SaveTwoSignalGadgetAsync(db);

        // Per-signal dismiss (the banner ✓): a DI-registered evaluator's signal defaults its
        // Source to the evaluator type name.
        await db.ClearAttentionAsync<GadgetEntity>(
            gadget.ID,
            AttentionClearFilter.Signal(nameof(GadgetReviewEvaluator), GadgetReviewEvaluator.ReviewCategory));

        Assert.True(gadget.HasActiveAttention);
        Assert.Equal(1, gadget.ActiveSignalCount);
        Assert.Equal(AttentionSeverity.Warning, gadget.HighestSeverity); // only LowStock left
    }

    [Fact]
    public async Task ClearMatchingNothing_IsNoOp_AndPreservesConcurrencyStamp()
    {
        await using var app = await StartGadgetAppAsync();
        using var scope = app.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AttentionTestDbContext>();

        var gadget = await SaveTwoSignalGadgetAsync(db);
        var stampBefore = gadget.LastSaveDate;

        await db.ClearAttentionAsync<GadgetEntity>(gadget.ID, AttentionClearFilter.ByScope("DoesNotExist"));

        // Nothing matched: both signals still active and the audit stamp (= concurrency version)
        // is untouched, so a client holding the pre-clear DTO won't hit a false 409.
        Assert.Equal(2, gadget.ActiveSignalCount);
        Assert.Equal(AttentionSeverity.Warning, gadget.HighestSeverity);
        Assert.Equal(stampBefore, gadget.LastSaveDate);
    }

    [Fact]
    public async Task IndexedMode_ByScope_ClearsMatchingRows_LeavesOthers()
    {
        await using var app = await AttentionApp.StartAsync(s => s
            .AddAttentionEvaluator<WidgetEntity, WidgetFlagEvaluator>()
            .AddAttentionEvaluator<WidgetEntity, WidgetReviewEvaluator>());
        using var scope = app.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AttentionTestDbContext>();

        var widget = new WidgetEntity { Name = "Sprocket", Flagged = true };
        db.Widgets.Add(widget);
        await WidgetRepository(db).SaveChangesAsync();
        Assert.Equal(2, db.Set<AttentionSignalEntry>().Count(x => x.ClearedAt == null));

        await db.ClearAttentionAsync<WidgetEntity>(
            widget.ID, AttentionClearFilter.ByScope(WidgetReviewEvaluator.ReviewScope));

        var rows = db.Set<AttentionSignalEntry>().ToList();
        Assert.NotNull(rows.Single(x => x.Category == WidgetReviewEvaluator.ReviewCategory).ClearedAt); // cleared
        Assert.Null(rows.Single(x => x.Category == WidgetFlagEvaluator.FlaggedCategory).ClearedAt);      // active

        // Summary recomputed on the entity row from the one remaining (Critical) signal.
        Assert.True(widget.HasActiveAttention);
        Assert.Equal(1, widget.ActiveSignalCount);
        Assert.Equal(AttentionSeverity.Critical, widget.HighestSeverity);
    }
}
