using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ShiftSoftware.ShiftEntity.Core;

/// <summary>
/// A single CRUD-endpoint declaration discovered from an entity's endpoint attribute. Drives both
/// the DI registration (default map + repository) and the minimal-API endpoint generation.
/// </summary>
public sealed class ShiftEntityEndpointSpec
{
    public required Type Entity { get; init; }
    public required Type ListDto { get; init; }
    public required Type ViewDto { get; init; }

    /// <summary>The custom repository type, or null to use the framework's built-in repository.</summary>
    public Type? Repository { get; init; }

    public required string Route { get; init; }
    public bool Secure { get; init; }
    public Type? ActionTreeType { get; init; }
    public string? ActionName { get; init; }
}

/// <summary>
/// Scans assemblies for entity classes decorated with the ShiftEntity endpoint attributes and
/// produces <see cref="ShiftEntityEndpointSpec"/>s. Used at startup by both the service registration
/// (to register repositories + default maps) and the endpoint generation (to map the routes).
/// </summary>
public static class ShiftEntityEndpointDiscovery
{
    public static IReadOnlyList<ShiftEntityEndpointSpec> Discover(IEnumerable<Assembly> assemblies)
    {
        var specs = new List<ShiftEntityEndpointSpec>();
        var routeOwners = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);

        foreach (var assembly in assemblies.Where(a => a is not null).Distinct())
        {
            IEnumerable<Type> types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.OfType<Type>(); }
            catch { continue; }

            // The endpoint attributes are only valid on entities, which all derive from ShiftEntityBase.
            // Narrowing to entity candidates here means GetCustomAttributes (which materializes attribute
            // instances) only runs for entity types, not every type in the assembly.
            foreach (var entityType in types.Where(IsEntityCandidate))
            {
                foreach (var attr in entityType.GetCustomAttributes(inherit: false).OfType<ShiftEntityEndpointAttributeBase>())
                {
                    var spec = BuildSpec(entityType, attr);

                    var normalized = NormalizeRoute(spec.Route);
                    if (routeOwners.TryGetValue(normalized, out var owner))
                        throw new InvalidOperationException(
                            $"Duplicate ShiftEntity endpoint route '{spec.Route}' on '{entityType.FullName}' — already declared by '{owner.FullName}'. " +
                            "Each attribute-driven endpoint must have a unique route. (This check covers other endpoint attributes only; " +
                            "a clash with a controller or manual map would instead surface as an AmbiguousMatchException at request time.)");

                    routeOwners[normalized] = entityType;
                    specs.Add(spec);
                }
            }
        }

        return specs;
    }

    // A concrete entity class — the only place the endpoint attributes are valid (all entities derive
    // from ShiftEntityBase). Used to skip non-entity types before reading their attributes.
    private static bool IsEntityCandidate(Type type)
        => type.IsClass
           && !type.IsAbstract
           && !type.IsGenericTypeDefinition
           && typeof(ShiftEntityBase).IsAssignableFrom(type);

    private static ShiftEntityEndpointSpec BuildSpec(Type entityType, ShiftEntityEndpointAttributeBase attr)
    {
        if (!IsShiftEntity(entityType))
            throw new InvalidOperationException(
                $"'{entityType.FullName}' has a ShiftEntity endpoint attribute but does not derive from ShiftEntity<>.");

        if (string.IsNullOrWhiteSpace(attr.Route))
            throw new InvalidOperationException($"'{entityType.FullName}' has a ShiftEntity endpoint attribute with an empty route.");

        // Generic arg layout:
        //   anonymous: [list, view] or [list, view, repo]
        //   secure:    [list, view, actionTree] or [list, view, actionTree, repo]
        var args = attr.GetType().GetGenericArguments();

        if (attr.IsSecure)
        {
            return new ShiftEntityEndpointSpec
            {
                Entity = entityType,
                ListDto = args[0],
                ViewDto = args[1],
                ActionTreeType = args[2],
                Repository = args.Length == 4 ? args[3] : null,
                ActionName = attr.ActionName,
                Route = attr.Route,
                Secure = true,
            };
        }

        return new ShiftEntityEndpointSpec
        {
            Entity = entityType,
            ListDto = args[0],
            ViewDto = args[1],
            Repository = args.Length == 3 ? args[2] : null,
            Route = attr.Route,
            Secure = false,
        };
    }

    private static bool IsShiftEntity(Type type)
    {
        for (var b = type.BaseType; b is not null; b = b.BaseType)
            if (b.IsGenericType && b.GetGenericTypeDefinition() == typeof(ShiftEntity<>))
                return true;

        return false;
    }

    private static string NormalizeRoute(string route) => "/" + route.Trim().Trim('/').ToLowerInvariant();
}
