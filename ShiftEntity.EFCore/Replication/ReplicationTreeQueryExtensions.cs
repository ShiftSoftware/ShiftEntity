using Microsoft.EntityFrameworkCore;

namespace ShiftSoftware.ShiftEntity.EFCore.Replication;

/// <summary>Loads roots with their replication trees.</summary>
public static class ReplicationTreeQueryExtensions
{
    /// <summary>Whether the model of <paramref name="context"/> declares a replication tree for <paramref name="rootType"/>.</summary>
    public static bool HasReplicationTree(this ShiftDbContext context, Type rootType) =>
        ReplicationTrees.For(context).ContainsKey(rootType);

    /// <summary>
    /// Adds the includes of <typeparamref name="TRoot"/>'s replication tree to the query, as a split query: the query
    /// that loads a root to build its document. Replication uses it after a save and in the catch-up, and a host's own
    /// backfill can use it too, so all of them build the same document.
    /// </summary>
    public static IQueryable<TRoot> IncludeReplicationTree<TRoot>(this IQueryable<TRoot> query, ShiftDbContext context)
        where TRoot : class
    {
        if (!ReplicationTrees.For(context).TryGetValue(typeof(TRoot), out var tree))
            throw new InvalidOperationException(
                $"'{typeof(TRoot).Name}' declares no replication tree in {context.GetType().Name}'s model. Declare it with " +
                $"modelBuilder.Entity<{typeof(TRoot).Name}>().HasReplicationTree(q => q.Include(...)), in OnModelCreating or in an IModelBuildingContributor.");

        return ((IQueryable<TRoot>)tree.Apply(query)).AsSplitQuery();
    }
}
