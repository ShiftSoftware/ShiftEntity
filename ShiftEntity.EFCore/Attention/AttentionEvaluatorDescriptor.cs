using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Attention;

namespace ShiftSoftware.ShiftEntity.EFCore.Attention;

/// <summary>
/// Metadata record for a DI-registered attention evaluator, created during
/// <c>AddAttentionEvaluator</c> registration. The pipeline uses these descriptors
/// to discover and invoke evaluators without generic dispatch at runtime.
/// </summary>
internal sealed class AttentionEvaluatorDescriptor
{
    /// <summary>The entity type or capability interface the evaluator targets.</summary>
    public required Type TargetType { get; init; }

    /// <summary>The evaluator's type name, used as the default <see cref="AttentionSignal.Source"/>.</summary>
    public required string EvaluatorTypeName { get; init; }

    /// <summary>Whether this evaluator implements <see cref="IRequiresAttentionHistory{TEntity}"/>.</summary>
    public required bool RequiresHistory { get; init; }

    /// <summary>Type-erased delegate that resolves the evaluator from DI and calls <c>Evaluate</c>.</summary>
    public required Func<IServiceProvider, object, object?, ActionTypes, AttentionSignal?> Invoke { get; init; }

    /// <summary>
    /// Type-erased delegate for history-aware evaluators. Resolves the evaluator and calls
    /// <c>EvaluateWithHistory</c>. <c>null</c> when <see cref="RequiresHistory"/> is <c>false</c>.
    /// </summary>
    public Func<IServiceProvider, object, object?, ActionTypes, IReadOnlyList<StoredAttentionSignal>, AttentionSignal?>? InvokeWithHistory { get; init; }
}
