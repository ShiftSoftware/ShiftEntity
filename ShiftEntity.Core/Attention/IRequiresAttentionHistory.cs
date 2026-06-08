using System.Collections.Generic;

namespace ShiftSoftware.ShiftEntity.Core.Attention;

/// <summary>
/// Opt-in variant of <see cref="IAttentionEvaluator{TEntity}"/> for evaluators that need
/// prior-signal history — typically nag or escalation rules. When implemented, the
/// framework loads prior signals scoped to this evaluator's <c>Source</c> + <c>Category</c>
/// for the same entity (bounded by a configurable re-raise window) and calls
/// <see cref="EvaluateWithHistory"/> instead of <see cref="IAttentionEvaluator{TEntity}.Evaluate"/>.
/// </summary>
/// <remarks>
/// Storage-agnostic — works identically whether the entity uses JSON-shadow storage
/// (<see cref="IHasAttention"/>) or indexed storage (<see cref="IHasIndexedAttention"/>).
/// History is loaded from whichever storage the entity uses.
///
/// You still implement the inherited <see cref="IAttentionEvaluator{TEntity}.Evaluate"/>;
/// the framework just won't call it on entities where history is available. A common
/// pattern is to return <c>null</c> from <c>Evaluate</c> so the rule fires only via the
/// history-aware path.
/// </remarks>
public interface IRequiresAttentionHistory<TEntity> : IAttentionEvaluator<TEntity>
    where TEntity : class
{
    /// <summary>
    /// Return a signal to raise attention, or <c>null</c> for no signal on this save.
    /// </summary>
    /// <param name="context">The standard evaluator context.</param>
    /// <param name="priorSignals">
    /// Active and cleared signals previously raised by this evaluator (matched on
    /// <c>Source</c> + <c>Category</c>) for the same entity, within the configured
    /// re-raise window. Empty if no prior signals exist.
    /// </param>
    AttentionSignal? EvaluateWithHistory(
        AttentionContext<TEntity> context,
        IReadOnlyList<StoredAttentionSignal> priorSignals);
}
