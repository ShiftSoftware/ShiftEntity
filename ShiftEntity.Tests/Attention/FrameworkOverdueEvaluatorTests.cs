using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Attention;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Attention;

/// <summary>
/// The framework's overdue rule raises once for each due date. A later save of the record, a background job's for
/// example, doesn't bring back a signal someone cleared. A new due date that has passed raises again.
/// </summary>
public class FrameworkOverdueEvaluatorTests
{
    private sealed class DueRecord : IHasDueDate
    {
        public DateTimeOffset? DueDate { get; init; }
    }

    private static readonly IServiceProvider Services = new ServiceCollection().BuildServiceProvider();

    private static AttentionSignal? Evaluate(DateTimeOffset? dueDate, params StoredAttentionSignal[] priorSignals)
        => new FrameworkOverdueEvaluator().EvaluateWithHistory(new AttentionContext<IHasDueDate>
        {
            Action = ActionTypes.Update,
            Entity = new DueRecord { DueDate = dueDate },
            Services = Services,
        }, priorSignals);

    private static StoredAttentionSignal Overdue(DateTimeOffset raisedAt, DateTimeOffset? clearedAt = null) => new()
    {
        Source = nameof(FrameworkOverdueEvaluator),
        Category = "Overdue",
        RaisedAt = raisedAt,
        ClearedAt = clearedAt,
    };

    [Fact]
    public void A_due_date_that_has_passed_raises_the_first_time()
    {
        var signal = Evaluate(DateTimeOffset.UtcNow.AddDays(-5));

        Assert.NotNull(signal);
        Assert.Equal("Overdue", signal.Category);
        Assert.Equal(nameof(FrameworkOverdueEvaluator), signal.Source);
    }

    [Fact]
    public void A_due_date_still_ahead_raises_nothing()
        => Assert.Null(Evaluate(DateTimeOffset.UtcNow.AddDays(5)));

    [Fact]
    public void No_due_date_raises_nothing()
        => Assert.Null(Evaluate(null));

    [Fact]
    public void A_signal_someone_cleared_is_not_raised_again_for_the_same_due_date()
    {
        var dueDate = DateTimeOffset.UtcNow.AddDays(-5);
        var cleared = Overdue(raisedAt: dueDate.AddDays(1), clearedAt: dueDate.AddDays(2));

        Assert.Null(Evaluate(dueDate, cleared));
    }

    [Fact]
    public void A_signal_still_active_for_the_same_due_date_is_not_raised_twice()
    {
        var dueDate = DateTimeOffset.UtcNow.AddDays(-5);

        Assert.Null(Evaluate(dueDate, Overdue(raisedAt: dueDate.AddHours(1))));
    }

    [Fact]
    public void A_moved_due_date_that_has_passed_raises_again()
    {
        // The first due date passed and was cleared; the date was then moved, and the new one has passed too.
        var firstDueDate = DateTimeOffset.UtcNow.AddDays(-10);
        var cleared = Overdue(raisedAt: firstDueDate.AddDays(1), clearedAt: firstDueDate.AddDays(2));
        var newDueDate = DateTimeOffset.UtcNow.AddDays(-1);

        Assert.NotNull(Evaluate(newDueDate, cleared));
    }

    [Fact]
    public void Another_category_from_the_same_source_does_not_count()
    {
        var dueDate = DateTimeOffset.UtcNow.AddDays(-5);
        var other = Overdue(raisedAt: dueDate.AddDays(1)) with { Category = "SomethingElse" };

        Assert.NotNull(Evaluate(dueDate, other));
    }

    [Fact]
    public void The_framework_hands_it_the_signals_raised_before()
        => Assert.IsAssignableFrom<IRequiresAttentionHistory<IHasDueDate>>(new FrameworkOverdueEvaluator());
}
