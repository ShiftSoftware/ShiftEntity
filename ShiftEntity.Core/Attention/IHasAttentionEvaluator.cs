namespace ShiftSoftware.ShiftEntity.Core.Attention;

/// <summary>
/// Lets an entity declare its own attention rule inline, without DI — implement on the
/// entity class alongside <see cref="IHasAttention"/> (or <see cref="IHasIndexedAttention"/>).
/// Use this for simple, self-contained rules where the entity has everything the rule
/// needs (e.g. status transitions). For rules that need DI dependencies or apply across
/// many entities via a capability interface, use <see cref="IAttentionEvaluator{TEntity}"/>
/// instead — the two paths <em>compose</em>, so an entity can have both.
/// </summary>
/// <remarks>
/// The framework runs both evaluator kinds together: an entity that implements
/// <see cref="IHasAttentionEvaluator{TSelf}"/> can also be evaluated by one or more
/// registered <see cref="IAttentionEvaluator{TEntity}"/> services on the same save, each
/// independently producing (or not producing) a signal. For evaluators that need
/// prior-signal history, use <see cref="IRequiresAttentionHistory{TEntity}"/>.
/// </remarks>
/// <typeparam name="TSelf">The entity type, by convention the implementing class itself.</typeparam>
public interface IHasAttentionEvaluator<TSelf> where TSelf : class
{
    /// <summary>
    /// Return a signal to raise attention, or <c>null</c> for no signal on this save.
    /// </summary>
    AttentionSignal? EvaluateAttention(AttentionContext<TSelf> context);
}
