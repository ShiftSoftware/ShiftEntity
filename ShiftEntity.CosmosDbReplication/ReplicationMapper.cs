using Microsoft.Extensions.DependencyInjection;
using ShiftMapper;

namespace ShiftSoftware.ShiftEntity.CosmosDbReplication;

/// <summary>
/// The mapping fallback for a <c>Replicate</c> / <c>UpdateReference</c> / <c>UpdatePropertyReference</c> registered
/// WITHOUT a mapping delegate: the host's ShiftMapper mapper, reached through <see cref="IShiftMapper"/> because a
/// library cannot name the application's mapper class.
/// <para>
/// Both pipelines call this ONCE per registered action, ABOVE the row loop, and never from inside it. That placement
/// is the whole design. Every failure on the per-row path is swallowed and recorded as "this row did not sync" —
/// the catch-up sweep's <c>catch</c>, the trigger's fire-and-forget task — so a mapper that was never registered,
/// resolved at the moment a row is mapped, would not throw; it would leave every row permanently dirty under a
/// clean-looking watermark. Resolving up front and checking the pair with <see cref="IShiftMapper.CanMap"/> turns
/// that into an <see cref="InvalidOperationException"/> out of the run, naming the operation, the pair and the fix.
/// </para>
/// <para>
/// <c>AddShiftMapper</c> is additive, so a host may register several mappers — a framework-supplied one such as
/// ShiftIdentity's replication mapper next to its own. Every registered <see cref="IShiftMapper"/> is consulted and
/// the LAST one that declares the pair wins, matching the container's own "last registration wins" rule for the
/// interface. The one door <see cref="IShiftMapper.CanMap"/> does not vouch for is the merge overload, which needs
/// the exact declared pair; that miss is ShiftMapper's own exception, and it is thrown before any row is written.
/// </para>
/// </summary>
internal static class ReplicationMapper
{
    /// <summary>
    /// The entity → document map for the create doors (<c>Replicate</c>, <c>UpdatePropertyReference</c>).
    /// </summary>
    /// <param name="operation">How the call site reads, for the exception: <c>Replicate&lt;BrandModel&gt;("Brands")</c>.</param>
    internal static Func<TEntity, TDocument> ResolveCreate<TEntity, TDocument>(IServiceProvider services, string operation)
        where TEntity : class
    {
        var mapper = Resolve<TEntity, TDocument>(services, operation);

        return entity => mapper.Map<TEntity, TDocument>(entity);
    }

    /// <summary>
    /// The entity → EXISTING document map for the merge door (<c>UpdateReference</c>): the stored document is
    /// handed in, the mapper writes the members it declares onto it, and the same object comes back — so a member
    /// the map ignores (typically the partition key) survives.
    /// </summary>
    internal static Func<TEntity, TDocument, TDocument> ResolveMerge<TEntity, TDocument>(IServiceProvider services, string operation)
        where TEntity : class
    {
        var mapper = Resolve<TEntity, TDocument>(services, operation);

        return (entity, existing) => mapper.Map<TEntity, TDocument>(entity, existing);
    }

    private static IShiftMapper Resolve<TEntity, TDocument>(IServiceProvider services, string operation)
    {
        var pair = $"'{typeof(TEntity).Name}' to '{typeof(TDocument).Name}'";

        var mappers = services.GetServices<IShiftMapper>().ToList();

        if (mappers.Count == 0)
            throw new InvalidOperationException(
                $"Replication {operation} passes no mapping delegate and no ShiftMapper mapper is registered, so " +
                $"there is nothing to map {pair} with. Register one with services.AddShiftMapper<YourMapper>() " +
                $"whose constructor declares CreateMap<{typeof(TEntity).Name}, {typeof(TDocument).Name}>() (or adds " +
                "a profile that does), or pass the mapping delegate explicitly.");

        //Last wins, matching what GetRequiredService<IShiftMapper>() would hand back when several are registered.
        var mapper = mappers.LastOrDefault(x => x.CanMap(typeof(TEntity), typeof(TDocument)));

        if (mapper is null)
            throw new InvalidOperationException(
                $"Replication {operation} passes no mapping delegate and none of the {mappers.Count} registered " +
                $"ShiftMapper mapper(s) ({string.Join(", ", mappers.Select(x => x.GetType().Name))}) declares a map " +
                $"{pair}. Add CreateMap<{typeof(TEntity).Name}, {typeof(TDocument).Name}>() to the mapper (or to a " +
                "profile it adds), or pass the mapping delegate explicitly.");

        return mapper;
    }
}
