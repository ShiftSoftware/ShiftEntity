using Microsoft.Extensions.DependencyInjection;
using ShiftMapper;
using ShiftSoftware.ShiftEntity.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace ShiftSoftware.ShiftEntity.EFCore;

/// <summary>
/// Startup validation for the mapping layer: every triple the app will serve either resolves a mapper, or the
/// app does not start.
/// <para>
/// Until this existed, every mapping gap was a FIRST-REQUEST failure. Nothing ever called
/// <c>AssertConfigurationIsValid</c>, and the AutoMapper registration it would have validated was built by a
/// deferred factory, so an uncovered triple sat silent through startup, through smoke tests, and surfaced as
/// a 500 on whichever endpoint a user happened to open first. This turns that into one boot-time error carrying the
/// COMPLETE list — which is the difference between "caught in CI" and "caught in production".
/// </para>
/// </summary>
public static class ShiftEntityMapperValidation
{
    /// <summary>
    /// Validates that each discovered triple resolves a mapper, and reports registry conflicts and codegen ABI
    /// skew. An uncovered triple is always fatal: there is no convention mapper behind it any more, so the
    /// alternative is a repository that throws on the first request that touches it.
    /// </summary>
    /// <param name="services">The built provider is not needed — validation is type-level, so this runs without booting the app.</param>
    /// <param name="assemblies">The same assemblies <c>RegisterShiftRepositories</c> scanned.</param>
    public static void Validate(IServiceCollection services, IReadOnlyList<Assembly> assemblies)
    {
        EnsureRegistryPopulated(assemblies);

        var problems = new List<string>();

        // ShiftMapper's answer for the triples nothing else covers. Built from the registrations alone — the
        // generated mappers are parameterless and the question is type-level (CanMap), so no provider is booted
        // — and only when a triple asks: the registry lookup above it answers first.
        IMapper? shiftMapper = null;
        bool shiftMapperBuilt = false;

        IMapper? ShiftMapper()
        {
            if (!shiftMapperBuilt)
            {
                shiftMapperBuilt = true;
                shiftMapper = BuildShiftMapper(services);
            }

            return shiftMapper;
        }

        // ── uncovered triples ─────────────────────────────────────────────────────────────────────────────
        foreach (var (triple, repository) in DiscoverTriples(assemblies))
        {
            if (ResolvesAMapper(services, triple, repository, ShiftMapper)) continue;

            problems.Add(
                $"  ({triple.Entity.Name}, {triple.ListDto.Name}, {triple.ViewDto.Name}) — no mapper. " +
                "Check that the assembly declaring the repository (or the [ShiftEntityEndpoint] entity) is " +
                "passed to RegisterShiftRepositories or that the host calls AddShiftMapper(), so its generated " +
                "ShiftMapper maps are registered; or add a [ShiftEntityMapper] partial class, call " +
                "UseMapper/UseGeneratedMapper in the repository, or override the mapping methods.");
        }

        // ── registry conflicts ────────────────────────────────────────────────────────────────────────────
        // Recorded rather than thrown at Register time, because that runs in a module initializer where an
        // exception becomes an unreadable TypeInitializationException. This is where they become readable.
        foreach (var conflict in ShiftEntityMapperRegistry.Conflicts)
            problems.Add($"  {conflict}");

        // ── version skew, detected rather than declared ────────────────────────────────────────────────────
        // JIT-preparing each mapper method resolves its call targets, so a member the mapper was compiled
        // against and that no longer exists throws HERE instead of on whichever endpoint a user opens first.
        // Nothing is versioned and nothing has to be remembered.
        foreach (var (mapperType, error) in ShiftEntityMapperRegistry.VerifyBindings())
            problems.Add(
                $"  {mapperType.FullName} cannot bind to this version of ShiftEntity: {error} " +
                $"Rebuild and republish '{mapperType.Assembly.GetName().Name}'. A generated mapper is code " +
                "frozen at its own build day, so it does not pick up framework changes until rebuilt.");

        if (problems.Count == 0) return;

        var message = new StringBuilder()
            .AppendLine($"ShiftEntity mapping validation failed ({problems.Count} problem(s)):")
            .AppendLine()
            .AppendLine(string.Join(Environment.NewLine, problems))
            .ToString();

        throw new InvalidOperationException(message);
    }

    /// <summary>
    /// A triple counts as covered by ANY of: an explicit DI registration, a source-generated mapper in the
    /// registry, the host's ShiftMapper declaring all four of its maps, or a repository that overrides the
    /// mapping methods itself.
    /// </summary>
    private static bool ResolvesAMapper(IServiceCollection services, MapperTriple triple, Type? repository, Func<IMapper?> shiftMapper)
    {
        var mapperInterface = typeof(IShiftEntityMapper<,,>)
            .MakeGenericType(triple.Entity, triple.ListDto, triple.ViewDto);

        if (services.Any(d => d.ServiceType == mapperInterface))
            return true;

        if (ShiftEntityMapperRegistry.Find(triple.Entity, triple.ListDto, triple.ViewDto) is not null)
            return true;

        if (shiftMapper() is { } mapper
            && mapper.CanMap(triple.Entity, triple.ViewDto)
            && mapper.CanMap(triple.ViewDto, triple.Entity)
            && mapper.CanMap(triple.Entity, triple.ListDto)
            && mapper.CanMap(triple.Entity, triple.Entity))
        {
            return true;
        }

        // The override test must be DeclaringType-based. Asking "does this type have a MapToView?" is true for
        // every repository, since ShiftRepository declares all four — so a naive check passes everything and
        // validates nothing.
        return repository is not null && OverridesAMappingMethod(repository);
    }

    /// <summary>
    /// A <see cref="Mapper"/> over the generated mappers the collection registers — the same ones the host's
    /// <c>IMapper</c> dispatches over, built without the host: each generated mapper is constructed
    /// parameterless, which is enough to answer <c>CanMap</c>. Null when nothing is registered.
    /// </summary>
    private static IMapper? BuildShiftMapper(IServiceCollection services)
    {
        var generated = services
            .Select(d => d.ServiceType)
            .Where(t => t.IsClass && !t.IsAbstract && typeof(ShiftMapperBase).IsAssignableFrom(t)
                        && Mapper.GeneratedIn(t.Assembly) == t)
            .Select(t => t.Assembly)
            .Distinct()
            .ToArray();

        return generated.Length == 0 ? null : Mapper.Create(generated);
    }

    private static readonly string[] MappingMethods = { "MapToView", "MapToEntity", "MapToList", "CopyEntity" };

    private static bool OverridesAMappingMethod(Type repository)
    {
        for (var t = repository; t is not null && t != typeof(object); t = t.BaseType)
        {
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ShiftRepository<,,,>))
                return false;

            if (t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                 .Any(m => MappingMethods.Contains(m.Name)))
                return true;
        }

        return false;
    }

    private readonly record struct MapperTriple(Type Entity, Type ListDto, Type ViewDto);

    private static IEnumerable<(MapperTriple Triple, Type? Repository)> DiscoverTriples(IReadOnlyList<Assembly> assemblies)
    {
        var seen = new HashSet<MapperTriple>();

        foreach (var spec in ShiftEntityEndpointDiscovery.Discover(assemblies))
        {
            var triple = new MapperTriple(spec.Entity, spec.ListDto, spec.ViewDto);
            if (seen.Add(triple)) yield return (triple, spec.Repository);
        }

        foreach (var type in assemblies.SelectMany(SafeTypes))
        {
            if (!type.IsClass || type.IsAbstract || type.ContainsGenericParameters) continue;

            for (var t = type.BaseType; t is not null; t = t.BaseType)
            {
                if (!t.IsGenericType || t.GetGenericTypeDefinition() != typeof(ShiftRepository<,,,>)) continue;

                var args = t.GetGenericArguments();
                var triple = new MapperTriple(args[1], args[2], args[3]);
                if (seen.Add(triple)) yield return (triple, type);
                break;
            }
        }
    }

    /// <summary>
    /// Reflection scans do not run module initializers, and the registry is populated by one the generator
    /// emits — so without this the registry can look empty and every triple would be reported as uncovered.
    /// Each assembly is wrapped: one bad consumer assembly must not take down startup for everyone else.
    /// </summary>
    private static void EnsureRegistryPopulated(IReadOnlyList<Assembly> assemblies)
    {
        foreach (var assembly in assemblies)
        {
            try { RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle); }
            catch { /* reported by the coverage check below if it actually mattered */ }
        }
    }

    private static IEnumerable<Type> SafeTypes(Assembly a)
    {
        try { return a.GetTypes(); }
        catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t is not null)!; }
    }
}
