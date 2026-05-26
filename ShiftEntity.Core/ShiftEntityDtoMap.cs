using ShiftSoftware.ShiftEntity.Model.Dtos;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;

namespace ShiftSoftware.ShiftEntity.Core;

/// <summary>
/// General-purpose registry mapping entity CLR type names to their ViewAndUpsertDTO types.
/// Built during <c>RegisterShiftRepositories</c> by scanning repository base-type generic
/// arguments. Used by attention endpoints to resolve hash-ID encoding per entity type.
/// </summary>
public sealed class ShiftEntityDtoMap
{
    private readonly Dictionary<string, Type> _entityToViewDtoType = new(StringComparer.Ordinal);

    /// <summary>Registers a mapping from an entity type name to its DTO type.</summary>
    public void Register(string entityTypeName, Type viewAndUpsertDtoType)
    {
        _entityToViewDtoType[entityTypeName] = viewAndUpsertDtoType;
    }

    /// <summary>Returns the DTO type for the given entity type name, or <c>null</c> if not registered.</summary>
    public Type? GetDtoType(string entityTypeName)
    {
        return _entityToViewDtoType.GetValueOrDefault(entityTypeName);
    }

    /// <summary>Tries to get the DTO type for the given entity type name.</summary>
    public bool TryGetDtoType(string entityTypeName, [MaybeNullWhen(false)] out Type dtoType)
    {
        return _entityToViewDtoType.TryGetValue(entityTypeName, out dtoType);
    }

    /// <summary>
    /// Scans the given assemblies for <see cref="ShiftRepositoryBase"/> subclasses and extracts
    /// entity → DTO type mappings from their generic arguments.
    /// </summary>
    internal void PopulateFromAssemblies(IEnumerable<Assembly> assemblies)
    {
        var repositoryTypes = assemblies
            .SelectMany(a => { try { return a.GetTypes(); } catch { return []; } })
            .Where(type => type.IsClass && !type.IsAbstract && typeof(ShiftRepositoryBase).IsAssignableFrom(type));

        foreach (var repositoryType in repositoryTypes)
        {
            if (repositoryType.BaseType is null)
                continue;

            var args = repositoryType.BaseType.GetGenericArguments();

            var entity = args.FirstOrDefault(x =>
                x.BaseType?.IsAssignableTo(typeof(ShiftEntityBase)) == true);

            var viewDto = args.FirstOrDefault(x =>
                x.BaseType?.IsAssignableTo(typeof(ShiftEntityViewAndUpsertDTO)) == true ||
                x.BaseType?.IsAssignableTo(typeof(ShiftEntityMixedDTO)) == true);

            if (entity is not null && viewDto is not null)
                _entityToViewDtoType[entity.Name] = viewDto;
        }
    }
}
