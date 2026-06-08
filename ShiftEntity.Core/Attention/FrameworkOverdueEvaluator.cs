using System;

namespace ShiftSoftware.ShiftEntity.Core.Attention;

/// <summary>
/// Framework-shipped evaluator that raises a warning when an entity's
/// <see cref="IHasDueDate.DueDate"/> has passed. Register with:
/// <code>services.AddAttentionEvaluator&lt;IHasDueDate, FrameworkOverdueEvaluator&gt;();</code>
/// Deduplication is handled by the framework pipeline — the same signal won't be
/// stored twice while an active (uncleared) signal with matching source+category exists.
/// </summary>
public sealed class FrameworkOverdueEvaluator : IAttentionEvaluator<IHasDueDate>
{
    public AttentionSignal? Evaluate(AttentionContext<IHasDueDate> context)
    {
        var dueDate = context.Entity.DueDate;

        if (dueDate is null || dueDate > DateTimeOffset.UtcNow)
            return null;

        return new AttentionSignal
        {
            Category = "Overdue",
            Source = nameof(FrameworkOverdueEvaluator),
            Reason = $"Due date ({dueDate:g}) has passed.",
            Severity = AttentionSeverity.Warning,
        };
    }
}
