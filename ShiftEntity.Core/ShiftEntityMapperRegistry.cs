using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace ShiftSoftware.ShiftEntity.Core;

/// <summary>
/// Runtime registry of source-generated mappers. Populated by module initializers the ShiftEntity
/// source generator emits. Two kinds:
/// - TRIPLE mappers, keyed by (entity, list DTO, view DTO) — consumed by
///   <c>ShiftRepositoryOptions.UseGeneratedMapper()</c> and endpoint discovery (<c>UseGeneratedMapper = true</c>).
/// - PAIR mappers (<see cref="IShiftObjectMapper{TEntity, TDto}"/>), keyed by (child entity, child DTO) —
///   consumed by the deep-mapping builder sugar (<c>ForEntityChildren</c>/<c>ForListChildren</c>). A pair's
///   conventions-only list projection expression is registered alongside it for SQL composition.
/// </summary>
public static class ShiftEntityMapperRegistry
{
    private static readonly ConcurrentDictionary<(Type Entity, Type ListDto, Type ViewDto), Type> mappers = new();
    private static readonly ConcurrentDictionary<(Type Entity, Type Dto), (Type MapperType, LambdaExpression? ListProjection)> pairs = new();

    private static readonly ConcurrentBag<RegistryConflict> conflicts = new();

    /// <summary>A second mapper claimed a triple that already had one. Recorded, never thrown — see Register.</summary>
    public sealed record RegistryConflict(Type Entity, Type ListDto, Type ViewDto, Type Kept, Type Rejected)
    {
        public override string ToString() =>
            $"({Entity.Name}, {ListDto.Name}, {ViewDto.Name}) is claimed by both " +
            $"{Kept.FullName} [{Kept.Assembly.GetName().Name}] (kept) and " +
            $"{Rejected.FullName} [{Rejected.Assembly.GetName().Name}] (ignored)";
    }

    /// <summary>
    /// Registers a generated mapper for a triple.
    /// <para>
    /// <b>Idempotent for the same type, deterministic for a different one, and it never throws.</b> This runs
    /// inside a <c>[ModuleInitializer]</c>, where an exception surfaces as a <c>TypeInitializationException</c>
    /// with the real cause buried — so a conflict is RECORDED here and reported as a readable aggregate at
    /// startup instead.
    /// </para>
    /// <para>
    /// The case this guards: a subclass of a PACKAGED repository resolves the same triple and registers a
    /// second, convention-only mapper built from configuration it cannot see. Last-write-wins made which one
    /// you got depend on module-initializer order — a coin flip, silently. Zero occurrences in tree today; the
    /// failure would be very hard to find if there ever were one.
    /// </para>
    /// </summary>
    public static void Register(Type entity, Type listDto, Type viewDto, Type mapperType)
    {
        var key = (entity, listDto, viewDto);

        var existing = mappers.GetOrAdd(key, mapperType);
        if (existing == mapperType)
            return;

        // Deterministic tie-break: prefer the mapper declared alongside its ENTITY. That is the one whose
        // generator run could actually see the entity's configuration; a mapper from another assembly was
        // generated without it.
        if (mapperType.Assembly == entity.Assembly && existing.Assembly != entity.Assembly)
        {
            mappers[key] = mapperType;
            conflicts.Add(new RegistryConflict(entity, listDto, viewDto, Kept: mapperType, Rejected: existing));
        }
        else
        {
            conflicts.Add(new RegistryConflict(entity, listDto, viewDto, Kept: existing, Rejected: mapperType));
        }
    }

    public static Type? Find(Type entity, Type listDto, Type viewDto)
        => mappers.TryGetValue((entity, listDto, viewDto), out var mapperType) ? mapperType : null;

    /// <summary>Every conflict recorded so far. Read by startup validation, which turns them into one error.</summary>
    public static IReadOnlyList<RegistryConflict> Conflicts => conflicts.ToList();

    /// <summary>
    /// Verifies that every registered mapper can still bind to the framework it is running against, and
    /// returns one entry per mapper that cannot.
    /// <para>
    /// A generated mapper is CODE frozen at its own assembly's build day — unlike an AutoMapper profile, which
    /// is data the host's AutoMapper interprets. It calls <c>MappingHelpers</c>, <c>ShiftMapperBuilder</c>,
    /// this registry and the taggable projection helpers <b>by exact signature</b>, and those calls are
    /// compiled into the consumer's own assembly. So when NuGet unifies the host to a newer ShiftEntity while
    /// a consumer package still carries the old call, the failure is a <c>MissingMethodException</c> at
    /// REQUEST time — on whichever endpoint a user happens to open.
    /// </para>
    /// <para>
    /// <b>Nothing needs to be versioned or bumped for this to work.</b> <c>PrepareMethod</c> JIT-compiles each
    /// mapper method, which resolves its call targets — so a genuinely missing member throws HERE, at startup,
    /// naming itself. That is strictly better than a hand-maintained ABI number, which fires on additive
    /// changes that break nothing and stays silent whenever somebody forgets to bump it.
    /// </para>
    /// <para>
    /// Only the three genuine binding failures are caught. Anything else is not what this is testing for and
    /// is left alone rather than turned into a spurious startup error.
    /// </para>
    /// </summary>
    public static IReadOnlyList<(Type MapperType, string Error)> VerifyBindings()
    {
        var broken = new List<(Type, string)>();

        foreach (var mapperType in mappers.Values.Distinct())
        {
            foreach (var method in Bindable(mapperType))
            {
                try
                {
                    System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(method.MethodHandle);
                }
                catch (MissingMethodException ex) { broken.Add((mapperType, ex.Message)); break; }
                catch (MissingFieldException ex) { broken.Add((mapperType, ex.Message)); break; }
                catch (TypeLoadException ex) { broken.Add((mapperType, ex.Message)); break; }
                catch { /* not a binding failure — not this check's business */ }
            }
        }

        return broken;
    }

    /// <summary>
    /// Constructors included: the list projection is built in a field initializer, so a break there lives in
    /// the constructor body. Open generics are skipped — they cannot be prepared without instantiation.
    /// </summary>
    private static IEnumerable<MethodBase> Bindable(Type mapperType)
    {
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic |
                                   BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        return mapperType.GetConstructors(Flags).Cast<MethodBase>()
            .Concat(mapperType.GetMethods(Flags))
            .Where(m => !m.IsAbstract && !m.ContainsGenericParameters);
    }

    /// <summary>Every registered triple. Startup validation needs this; there is no other enumeration path.</summary>
    public static IReadOnlyList<(Type Entity, Type ListDto, Type ViewDto, Type MapperType)> All() =>
        mappers.Select(kv => (kv.Key.Entity, kv.Key.ListDto, kv.Key.ViewDto, kv.Value)).ToList();

    public static void RegisterPair(Type entity, Type dto, Type mapperType, LambdaExpression? listProjection = null)
        => pairs[(entity, dto)] = (mapperType, listProjection);

    public static Type? FindPair(Type entity, Type dto)
        => pairs.TryGetValue((entity, dto), out var pair) ? pair.MapperType : null;

    public static LambdaExpression? FindPairListProjection(Type entity, Type dto)
        => pairs.TryGetValue((entity, dto), out var pair) ? pair.ListProjection : null;
}
