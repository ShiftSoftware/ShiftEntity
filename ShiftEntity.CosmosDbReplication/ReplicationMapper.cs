using Microsoft.Extensions.DependencyInjection;
using ShiftMapper;

namespace ShiftSoftware.ShiftEntity.CosmosDbReplication;

/// <summary>
/// The mapping fallback for a <c>Replicate</c> / <c>UpdateReference</c> / <c>UpdatePropertyReference</c> registered
/// WITHOUT a mapping delegate: the host's ShiftMapper <see cref="Mapper"/>, reached through <see cref="IMapper"/>
/// because a library cannot name the application's types — the typed methods the generator writes onto
/// <see cref="Mapper"/> are extension methods over the application's own pairs, and none of them can be chosen for
/// a type parameter.
/// <para>
/// Both pipelines call this ONCE per registered action, ABOVE the row loop, and never from inside it. That placement
/// is the whole design. Every failure on the per-row path is swallowed and recorded as "this row did not sync" —
/// the catch-up sweep's <c>catch</c>, the trigger's fire-and-forget task — so a mapper that was never registered,
/// resolved at the moment a row is mapped, would not throw; it would leave every row permanently dirty under a
/// clean-looking watermark. Resolving up front and checking the pair with <see cref="IMapper.CanMap"/> turns
/// that into an <see cref="InvalidOperationException"/> out of the run, naming the operation, the pair and the fix.
/// </para>
/// <para>
/// <see cref="IMapper"/> is ONE door however many assemblies register. <c>AddShiftMapper</c> keeps a single registry
/// per collection — a framework's own registration (ShiftIdentity's, say) and the application's land in the same
/// one, in any order — and registers <see cref="Mapper"/> once, over the GENERATED mapper of every assembly that
/// called it; each pair is answered by the first registered generated mapper declaring it, the application's own
/// first because it re-bakes every package's maps. Two mapper classes each writing their OWN map for a pair never
/// reach this code: ShiftMapper refuses that at build time (SM0042). So this resolves the one door and asks it; it
/// does not enumerate mappers or choose between them. The one thing <see cref="IMapper.CanMap"/> does not vouch for
/// is the merge overload, which needs the exact declared pair; that miss is ShiftMapper's own exception, and it is
/// thrown before any row is written.
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

    private static IMapper Resolve<TEntity, TDocument>(IServiceProvider services, string operation)
    {
        var pair = $"'{typeof(TEntity).Name}' to '{typeof(TDocument).Name}'";

        var mapper = services.GetService<IMapper>();

        //A Mapper with nothing registered is what AddShiftMapper leaves behind when it was called only from an
        //assembly that declares no map: the run-time door exists and has nothing to dispatch to. Same fix, so the
        //same message — ShiftMapper's own would talk about Mapper.Create, which is not the host's problem.
        if (mapper is null || mapper is Mapper { Registered.Count: 0 })
            throw new InvalidOperationException(
                $"Replication {operation} passes no mapping delegate and no ShiftMapper mapper is registered, so " +
                $"there is nothing to map {pair} with. Declare CreateMap<{typeof(TEntity).Name}, {typeof(TDocument).Name}>() " +
                "in a ShiftMapperBase class and call services.AddShiftMapper() from that assembly (or from one that " +
                "references it), or pass the mapping delegate explicitly.");

        if (!mapper.CanMap(typeof(TEntity), typeof(TDocument)))
        {
            //Mapper is what AddShiftMapper registers: one door over the generated mapper of every assembly that
            //called it. The generated classes all carry the same name, so it is their ASSEMBLIES that tell the host
            //where it expected the pair to be declared.
            var registered = mapper is Mapper door
                ? door.Registered.Select(x => x.GetType().Assembly.GetName().Name ?? x.GetType().Name).ToList()
                : [mapper.GetType().Name];

            throw new InvalidOperationException(
                $"Replication {operation} passes no mapping delegate and none of the {registered.Count} registered " +
                $"ShiftMapper mapper(s) ({string.Join(", ", registered)}) declares a map {pair}. Add " +
                $"CreateMap<{typeof(TEntity).Name}, {typeof(TDocument).Name}>() to a mapper class one of those " +
                "assemblies can see, or pass the mapping delegate explicitly.");
        }

        return mapper;
    }
}
