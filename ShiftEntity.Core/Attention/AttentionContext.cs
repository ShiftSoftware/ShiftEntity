using System;

namespace ShiftSoftware.ShiftEntity.Core.Attention;

/// <summary>
/// What the framework passes to every evaluator. Strongly typed on the entity (or, for
/// capability-bound evaluators, on the capability interface — e.g. <c>IHasDueDate</c>).
/// </summary>
/// <typeparam name="TEntity">The entity type or capability interface the evaluator targets.</typeparam>
public sealed class AttentionContext<TEntity> where TEntity : class
{
    /// <summary>Whether the entity is being inserted or updated on this save.</summary>
    public required ActionTypes Action { get; init; }

    /// <summary>The entity's post-save (intended-new) state.</summary>
    public required TEntity Entity { get; init; }

    /// <summary>
    /// The entity's pre-save state, materialized by the framework from EF Core's
    /// <c>ChangeTracker</c> (<c>db.Entry(entity).OriginalValues.ToObject()</c>).
    /// <c>null</c> on Insert. Use this together with <see cref="Entity"/> for
    /// transition-based rules (e.g. "fire only when Status moves to X").
    /// </summary>
    public TEntity? Original { get; init; }

    /// <summary>
    /// Scope-local service provider, for evaluators that need to resolve dependencies
    /// ad-hoc. Most evaluators won't touch this — they should take what they need via
    /// constructor injection instead.
    /// </summary>
    public required IServiceProvider Services { get; init; }
}
