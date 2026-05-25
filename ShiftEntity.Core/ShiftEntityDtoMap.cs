using ShiftSoftware.ShiftEntity.Model.Dtos;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;

namespace ShiftSoftware.ShiftEntity.Core;

public sealed class ShiftEntityDtoMap
{
    private readonly Dictionary<string, Type> _entityToViewDtoType = new(StringComparer.Ordinal);

    public void Register(string entityTypeName, Type viewAndUpsertDtoType)
    {
        _entityToViewDtoType[entityTypeName] = viewAndUpsertDtoType;
    }

    public Type? GetDtoType(string entityTypeName)
    {
        return _entityToViewDtoType.GetValueOrDefault(entityTypeName);
    }

    public bool TryGetDtoType(string entityTypeName, [MaybeNullWhen(false)] out Type dtoType)
    {
        return _entityToViewDtoType.TryGetValue(entityTypeName, out dtoType);
    }

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
