using Microsoft.Extensions.DependencyInjection;
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
/// Until this existed, every mapping gap was a FIRST-REQUEST failure. There is no
/// <c>AssertConfigurationIsValid</c> anywhere in the tree and the AutoMapper registration uses a deferred
/// factory, so an uncovered triple sat silent through startup, through smoke tests, and surfaced as a 500 on
/// whichever endpoint a user happened to open first. This turns that into one boot-time error carrying the
/// COMPLETE list — which is the difference between "caught in CI" and "caught in production".
/// </para>
/// </summary>
public static class ShiftEntityMapperValidation
{
    /// <summary>
    /// Validates that each discovered triple resolves a mapper, and reports registry conflicts and codegen ABI
    /// skew. Under <see cref="ShiftEntityMappingMode.GeneratedOnly"/> an uncovered triple is fatal; under the
    /// other modes it is not, because AutoMapper is still there to catch it.
    /// </summary>
    /// <param name="services">The built provider is not needed — validation is type-level, so this runs without booting the app.</param>
    /// <param name="assemblies">The same assemblies <c>RegisterShiftRepositories</c> scanned.</param>
    /// <param name="mode">The configured mapping mode.</param>
    public static void Validate(IServiceCollection services, IReadOnlyList<Assembly> assemblies, ShiftEntityMappingMode mode)
    {
        EnsureRegistryPopulated(assemblies);

        var problems = new List<string>();

        // ── uncovered triples ─────────────────────────────────────────────────────────────────────────────
        if (mode == ShiftEntityMappingMode.GeneratedOnly)
        {
            foreach (var (triple, repository) in DiscoverTriples(assemblies))
            {
                if (ResolvesAMapper(services, triple, repository)) continue;

                problems.Add(
                    $"  ({triple.Entity.Name}, {triple.ListDto.Name}, {triple.ViewDto.Name}) — no mapper. " +
                    "Add a [ShiftEntityMapper] partial class, call UseMapper/UseGeneratedMapper in the " +
                    "repository, or override the mapping methods.");
            }
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
            .AppendLine($"ShiftEntity mapping validation failed ({problems.Count} problem(s)), mode = {mode}:")
            .AppendLine()
            .AppendLine(string.Join(Environment.NewLine, problems))
            .ToString();

        throw new InvalidOperationException(message);
    }

    /// <summary>
    /// A triple counts as covered by ANY of: an explicit DI registration, a source-generated mapper in the
    /// registry, or a repository that overrides the mapping methods itself.
    /// </summary>
    private static bool ResolvesAMapper(IServiceCollection services, MapperTriple triple, Type? repository)
    {
        var mapperInterface = typeof(IShiftEntityMapper<,,>)
            .MakeGenericType(triple.Entity, triple.ListDto, triple.ViewDto);

        if (services.Any(d => d.ServiceType == mapperInterface))
            return true;

        if (ShiftEntityMapperRegistry.Find(triple.Entity, triple.ListDto, triple.ViewDto) is not null)
            return true;

        // The override test must be DeclaringType-based. Asking "does this type have a MapToView?" is true for
        // every repository, since ShiftRepository declares all four — so a naive check passes everything and
        // validates nothing.
        return repository is not null && OverridesAMappingMethod(repository);
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
