using Microsoft.Extensions.DependencyInjection;

namespace ShiftSoftware.ShiftEntity.Core.Attention;

/// <summary>Helper methods for evaluators, built on <see cref="IEntityViewerTracker"/>.</summary>
public static class AttentionViewerExtensions
{
    /// <summary>
    /// Whether someone is viewing the entity under evaluation right now. This is a single call
    /// that wraps the documented <c>GetService</c> +
    /// <see cref="IEntityViewerTracker.HasViewers"/> pattern. It is meant for evaluators that
    /// skip raising while a viewer already sees the change in real time. With a <c>null</c>
    /// <paramref name="scope"/> (the default), any viewer entry for the record counts; with a
    /// non-null <paramref name="scope"/>, only viewers of that exact scope count — see
    /// <see cref="IEntityViewerTracker.HasViewers"/> for the matching rules.
    /// </summary>
    /// <remarks>
    /// Follows the tracker contract: when presence cannot be checked, the result is
    /// <c>false</c>, so the caller raises as normal. That happens when no tracker is
    /// registered, when the entity is not a <see cref="ShiftEntityBase"/>, or when the entity
    /// has no database ID yet (an Insert — nobody can be viewing a record that does not exist
    /// yet). The tracker is keyed on the entity's runtime CLR short name, not on
    /// <typeparamref name="TEntity"/>: a capability-bound evaluator targets an interface, and
    /// presence uses the same names as <see cref="AttentionRaised.EntityType"/>.
    /// </remarks>
    public static bool HasActiveViewers<TEntity>(this AttentionContext<TEntity> context, string? scope = null)
        where TEntity : class
    {
        var tracker = context.Services.GetService<IEntityViewerTracker>();
        if (tracker is null)
            return false;

        return context.Entity is ShiftEntityBase entity
            && entity.ID != 0
            && tracker.HasViewers(entity.GetType().Name, entity.ID, scope);
    }
}
