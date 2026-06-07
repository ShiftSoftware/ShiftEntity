using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.Core.Attention;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.EFCore.Entities;
using ShiftSoftware.ShiftEntity.Tests.Attention.Scenario;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Attention;

/// <summary>
/// Iteration 7 — emission. Every newly-raised (post-dedup) signal publishes one
/// <see cref="AttentionRaised"/> event through the channel dispatcher
/// (<c>AddAttentionEmission</c> / <c>AddAttentionConsumer&lt;T&gt;</c>) <em>after</em> the
/// save commits; suppressed signals publish nothing; apps that never register the
/// dispatcher see no behavior change; a failing consumer is isolated. "Nothing published"
/// is asserted via a sentinel event rather than a sleep — the single-reader drain is FIFO,
/// so the sentinel arriving proves anything published before it arrived too.
/// </summary>
public class AttentionEmissionTests
{
    private static ShiftRepository<AttentionTestDbContext, GadgetEntity, GadgetDTO, GadgetDTO> GadgetRepository(
        AttentionTestDbContext db)
        => new(db, new ThrowingGadgetMapper());

    private static ShiftRepository<AttentionTestDbContext, WidgetEntity, WidgetDTO, WidgetDTO> WidgetRepository(
        AttentionTestDbContext db)
        => new(db, new ThrowingWidgetMapper());

    private static AttentionRaised Sentinel() => new()
    {
        EntityType = "__Sentinel__",
        EntityId = -1,
        Signal = new StoredAttentionSignal { Source = "Test", Category = "Sentinel", RaisedAt = DateTimeOffset.UtcNow },
    };

    [Fact]
    public async Task JsonMode_Insert_PublishesOneEventWithFullContract()
    {
        await using var app = await AttentionApp.StartAsync(s => s.AddAttentionConsumer<RecordingAttentionConsumer>());
        using var scope = app.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<AttentionTestDbContext>();
        var gadget = new GadgetEntity { Name = "Anvil", StockLevel = 2 };
        db.Gadgets.Add(gadget);

        await GadgetRepository(db).SaveChangesAsync();

        var received = await app.Sink.WaitUntilAsync(s => s.Count >= 1);

        var (consumer, evt) = Assert.Single(received);
        Assert.Equal(nameof(RecordingAttentionConsumer), consumer);
        Assert.Equal(nameof(GadgetEntity), evt.EntityType);
        Assert.True(gadget.ID > 0, "the insert should have assigned a database ID");
        Assert.Equal(gadget.ID, evt.EntityId);
        // Self-evaluator signals default Source to the entity's CLR type name.
        Assert.Equal(nameof(GadgetEntity), evt.Signal.Source);
        Assert.Equal(GadgetEntity.LowStockCategory, evt.Signal.Category);
        Assert.Equal(AttentionSeverity.Warning, evt.Signal.Severity);
        Assert.Equal("Stock is down to 2", evt.Signal.Reason);
        Assert.NotEqual(default, evt.Signal.RaisedAt);
        Assert.Null(evt.Signal.ClearedAt);
    }

    [Fact]
    public async Task UpdateIntoTriggerCondition_PublishesEvent()
    {
        await using var app = await AttentionApp.StartAsync(s => s.AddAttentionConsumer<RecordingAttentionConsumer>());
        using var scope = app.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<AttentionTestDbContext>();
        var repository = GadgetRepository(db);

        var gadget = new GadgetEntity { Name = "Anvil", StockLevel = 10 };
        db.Gadgets.Add(gadget);
        await repository.SaveChangesAsync();

        gadget.StockLevel = 1;
        await repository.SaveChangesAsync();

        var received = await app.Sink.WaitUntilAsync(s => s.Count >= 1);

        var (_, evt) = Assert.Single(received);
        Assert.Equal(gadget.ID, evt.EntityId);
        Assert.Equal(GadgetEntity.LowStockCategory, evt.Signal.Category);
    }

    [Fact]
    public async Task DedupSuppressedSignal_PublishesNoEvent()
    {
        await using var app = await AttentionApp.StartAsync(s => s.AddAttentionConsumer<RecordingAttentionConsumer>());
        using var scope = app.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<AttentionTestDbContext>();
        var repository = GadgetRepository(db);

        var gadget = new GadgetEntity { Name = "Anvil", StockLevel = 2 };
        db.Gadgets.Add(gadget);
        await repository.SaveChangesAsync();

        // Still low on the second save: the state-based evaluator re-raises the raw signal,
        // and the framework dedup (active signal, same Source+Category) suppresses it.
        gadget.Name = "Anvil (renamed)";
        await repository.SaveChangesAsync();

        await app.Provider.GetRequiredService<IAttentionDispatcher>()
            .PublishAsync(Sentinel(), TestContext.Current.CancellationToken);

        var received = await app.Sink.WaitUntilAsync(s => s.Any(r => r.Event.EntityType == "__Sentinel__"));

        Assert.Single(received, r => r.Event.EntityType == "__Sentinel__");
        var gadgetEvent = Assert.Single(received, r => r.Event.EntityType == nameof(GadgetEntity));
        Assert.Equal(GadgetEntity.LowStockCategory, gadgetEvent.Event.Signal.Category);
    }

    [Fact]
    public async Task NoDispatcherRegistered_SaveSucceedsAndPersistsSignal()
    {
        // No AddAttentionEmission / AddAttentionConsumer — the not-opted-in app.
        await using var app = await AttentionApp.StartAsync();
        using var scope = app.CreateScope();

        Assert.Null(app.Provider.GetService<IAttentionDispatcher>());

        var db = scope.ServiceProvider.GetRequiredService<AttentionTestDbContext>();
        var gadget = new GadgetEntity { Name = "Anvil", StockLevel = 2 };
        db.Gadgets.Add(gadget);

        await GadgetRepository(db).SaveChangesAsync();

        // The Phase 1 pipeline still ran and persisted the signal — only emission is absent.
        Assert.True(gadget.HasActiveAttention);
        Assert.Equal(1, gadget.ActiveSignalCount);
        Assert.Equal(AttentionSeverity.Warning, gadget.HighestSeverity);
        Assert.Empty(app.Sink.Snapshot());
    }

    [Fact]
    public async Task ThrowingConsumer_IsIsolated_AllOtherConsumersStillReceive()
    {
        await using var app = await AttentionApp.StartAsync(s => s
            // Registered first, so it runs first — the recorders only receive the event
            // if the drain loop isolates the failure.
            .AddAttentionConsumer<ThrowingAttentionConsumer>()
            .AddAttentionConsumer<RecordingAttentionConsumer>()
            .AddAttentionConsumer<SecondRecordingAttentionConsumer>());
        using var scope = app.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<AttentionTestDbContext>();
        var gadget = new GadgetEntity { Name = "Anvil", StockLevel = 2 };
        db.Gadgets.Add(gadget);

        await GadgetRepository(db).SaveChangesAsync();

        var received = await app.Sink.WaitUntilAsync(s => s.Count >= 2);

        Assert.Equal(2, received.Count);
        Assert.Contains(received, r => r.Consumer == nameof(RecordingAttentionConsumer));
        Assert.Contains(received, r => r.Consumer == nameof(SecondRecordingAttentionConsumer));
        Assert.All(received, r => Assert.Equal(gadget.ID, r.Event.EntityId));
    }

    [Fact]
    public async Task IndexedMode_InsertThroughTransactionalPath_PublishesAfterCommitWithAssignedId()
    {
        await using var app = await AttentionApp.StartAsync(s => s
            .AddAttentionEvaluator<WidgetEntity, WidgetFlagEvaluator>()
            .AddAttentionConsumer<RecordingAttentionConsumer>());
        using var scope = app.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<AttentionTestDbContext>();
        var widget = new WidgetEntity { Name = "Sprocket", Flagged = true };
        db.Widgets.Add(widget);

        // IHasIndexedAttention forces the transactional save path; on Insert the signal row
        // is flushed in a second save once the entity ID exists.
        await WidgetRepository(db).SaveChangesAsync();

        var received = await app.Sink.WaitUntilAsync(s => s.Count >= 1);

        var (_, evt) = Assert.Single(received);
        Assert.Equal(nameof(WidgetEntity), evt.EntityType);
        Assert.True(widget.ID > 0, "the insert should have assigned a database ID");
        Assert.Equal(widget.ID, evt.EntityId);
        // DI-registered evaluator signals default Source to the evaluator's type name.
        Assert.Equal(nameof(WidgetFlagEvaluator), evt.Signal.Source);
        Assert.Equal(WidgetFlagEvaluator.FlaggedCategory, evt.Signal.Category);
        Assert.Equal(AttentionSeverity.Critical, evt.Signal.Severity);

        // The event matches what the indexed table persisted.
        var entry = Assert.Single(db.Set<AttentionSignalEntry>().ToList());
        Assert.Equal(widget.ID, entry.EntityId);
        Assert.Equal(nameof(WidgetEntity), entry.EntityType);
    }
}
