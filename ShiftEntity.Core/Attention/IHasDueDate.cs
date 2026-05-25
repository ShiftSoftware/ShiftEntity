using System;

namespace ShiftSoftware.ShiftEntity.Core.Attention;

/// <summary>
/// Capability interface for entities with a due date. Implement this to opt into the
/// framework's <see cref="FrameworkOverdueEvaluator"/>, which raises a warning-severity
/// attention signal when the due date has passed.
/// </summary>
public interface IHasDueDate
{
    DateTimeOffset? DueDate { get; }
}
