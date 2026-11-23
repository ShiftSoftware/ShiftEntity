using System;
using System.Collections.Generic;
using System.Linq;

namespace ShiftSoftware.ShiftEntity.Core.Attention;

/// <summary>
/// Framework-shipped evaluator that raises a warning when an entity's
/// <see cref="IHasDueDate.DueDate"/> has passed. Register with:
/// <code>services.AddAttentionEvaluator&lt;IHasDueDate, FrameworkOverdueEvaluator&gt;();</code>
/// </summary>
/// <remarks>
/// <para>
/// It raises once for each due date. After it has raised for a due date that had passed, later saves of the record
/// don't raise it again, whoever makes them, a background job for example. A signal someone cleared stays cleared.
/// Moving the due date gives the record a new due date, which raises again once it has passed.
/// </para>
/// <para>
/// Evaluators run when the record is saved, so a due date that passes between two saves raises nothing until the next
/// save of the record.
/// </para>
/// </remarks>
public sealed class FrameworkOverdueEvaluator : IRequiresAttentionHistory<IHasDueDate>
{
    private const string OverdueCategory = "Overdue";

    /// <summary>
    /// Whether the due date has passed, without the signals raised before. The framework calls
    /// <see cref="EvaluateWithHistory"/> instead.
    /// </summary>
    public AttentionSignal? Evaluate(AttentionContext<IHasDueDate> context) => Overdue(context.Entity.DueDate);

    /// <inheritdoc/>
    public AttentionSignal? EvaluateWithHistory(AttentionContext<IHasDueDate> context, IReadOnlyList<StoredAttentionSignal> priorSignals)
    {
        var dueDate = context.Entity.DueDate;

        // A signal raised once this due date had passed, active or cleared, already said the record is overdue.
        if (dueDate is not null && priorSignals.Any(signal => signal.Category == OverdueCategory && signal.RaisedAt >= dueDate))
            return null;

        return Overdue(dueDate);
    }

    private static AttentionSignal? Overdue(DateTimeOffset? dueDate)
    {
        if (dueDate is null || dueDate > DateTimeOffset.UtcNow)
            return null;

        return new AttentionSignal
        {
            Category = OverdueCategory,
            Source = nameof(FrameworkOverdueEvaluator),
            Reason = $"Due date ({dueDate:g}) has passed.",
            Severity = AttentionSeverity.Warning,
        };
    }
}
