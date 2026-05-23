namespace ShiftSoftware.ShiftEntity.Core.Attention;

/// <summary>
/// A DI-registered evaluator that produces attention signals for a given entity, or for
/// any entity matching a capability interface (e.g. <c>IHasDueDate</c>). Use this when
/// the rule needs constructor dependencies, applies across many entity types through a
/// capability interface, or lives in framework / shared code. For simple, dependency-free
/// rules that live naturally on a single entity, use <see cref="IHasAttentionEvaluator{TSelf}"/>
/// instead — the two paths <em>compose</em>, so an entity can have both.
/// </summary>
/// <remarks>
/// Register with:
/// <code>
/// services.AddAttentionEvaluator&lt;Invoice, InvoiceBudgetEvaluator&gt;();
/// services.AddAttentionEvaluator&lt;IHasDueDate, FrameworkOverdueEvaluator&gt;();
/// </code>
/// Multiple <see cref="IAttentionEvaluator{TEntity}"/> services can be registered against
/// the same entity (or against capability interfaces it implements); all execute on each
/// save and may each produce a signal. Evaluators that need prior-signal history use the
/// derived <see cref="IRequiresAttentionHistory{TEntity}"/> instead.
/// </remarks>
/// <typeparam name="TEntity">
/// The entity type, or a capability interface (e.g. <c>IHasDueDate</c>) implemented by
/// the entities that should be evaluated.
/// </typeparam>
public interface IAttentionEvaluator<TEntity> where TEntity : class
{
    /// <summary>
    /// Return a signal to raise attention, or <c>null</c> for no signal on this save.
    /// </summary>
    AttentionSignal? Evaluate(AttentionContext<TEntity> context);
}
