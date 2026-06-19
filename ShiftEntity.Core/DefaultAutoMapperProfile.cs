using AutoMapper;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.ShiftEntity.Model;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Reflection;

namespace ShiftSoftware.ShiftEntity.Core;

public class DefaultAutoMapperProfile : Profile
{
    // (source, dest) pairs this profile instance already created — prevents an in-profile duplicate
    // (AutoMapper errors on that). Only consulted on the explicit/deduped path.
    private readonly HashSet<(Type Source, Type Destination)> _created = new();

    public DefaultAutoMapperProfile() { }
    public DefaultAutoMapperProfile(params Assembly[] assemblies)
    {
        CreateMap<List<ShiftFileDTO>?, string?>().ConvertUsing<ListOfShiftFileDtoToString>();
        CreateMap<string?, List<ShiftFileDTO>?>().ConvertUsing<StringToListOfShiftFileDto>();

        CreateMap<ShiftEntityBase, ShiftEntitySelectDTO?>().ConvertUsing<ShiftEntityToShiftEntitySelectDTO>();
        CreateMap<ShiftEntitySelectDTO, ShiftEntityBase>().ConstructUsing(x => null);

        var repositoryTypes = assemblies
            .SelectMany(x => x.GetTypes())
            .Where(type => type.IsClass && !type.IsAbstract && typeof(ShiftRepositoryBase).IsAssignableFrom(type))
            .ToList();

        foreach (var repositoryType in repositoryTypes)
        {
            if (repositoryType.BaseType is not null)
                CreateEntityMaps(repositoryType.BaseType.GetGenericArguments());
        }
    }

    /// <summary>
    /// Builds maps for attribute-driven endpoints whose entities have no repository class for the
    /// assembly scanner to discover. Each item is the generic argument set (entity + DTOs).
    /// <paramref name="alreadyConfiguredPairs"/> are the (source, dest) pairs other profiles (the
    /// repository scan + user profiles) already declared; any such pair is skipped so an existing or
    /// customized map is never overwritten. The global type converters added by the assembly-scanning
    /// constructor are intentionally not re-added here, so the two profiles compose cleanly.
    /// </summary>
    public DefaultAutoMapperProfile(
        IEnumerable<Type[]> explicitGenericArgumentSets,
        ISet<(Type Source, Type Destination)> alreadyConfiguredPairs)
    {
        foreach (var genericArguments in explicitGenericArgumentSets)
            CreateEntityMaps(genericArguments, alreadyConfiguredPairs);
    }

    // Single source of truth for entity → DTO map creation, shared by the assembly-scanning path
    // (skipPairs == null → original behavior, unchanged) and the explicit endpoint path
    // (skipPairs != null → never overrides a pair another profile already declared). Picks the
    // entity / list DTO / view (or mixed) DTO out of the supplied generic arguments.
    private void CreateEntityMaps(Type[] genericArguments, ISet<(Type Source, Type Destination)>? skipPairs = null)
    {
        var entity = genericArguments
            .Where(x => x.BaseType is not null)
            .Where(x => x.BaseType!.IsAssignableTo(typeof(ShiftEntityBase)))
            .FirstOrDefault();

        var listDto = genericArguments
            .Where(x => x.BaseType is not null)
            .Where(x => x.BaseType!.IsAssignableTo(typeof(ShiftEntityListDTO)))
            .FirstOrDefault();

        var viewAndUpsertDTOorMixedDTO = genericArguments
            .Where(x => x.BaseType is not null)
            .Where(x => x.BaseType!.IsAssignableTo(typeof(ShiftEntityViewAndUpsertDTO)) || x.BaseType!.IsAssignableTo(typeof(ShiftEntityMixedDTO)))
            .FirstOrDefault();

        if (entity is null)
            return;

        if (listDto is not null && CanCreate(entity, listDto, skipPairs))
            CreateMap(entity, listDto);

        if (viewAndUpsertDTOorMixedDTO is not null && CanCreate(entity, viewAndUpsertDTOorMixedDTO, skipPairs))
        {
            CreateMap(entity, viewAndUpsertDTOorMixedDTO)
                .DefaultEntityToDtoAfterMap()
                .ReverseMap()
                .DefaultDtoToEntityAfterMap();
        }
    }

    // Repository-scan path (skipPairs == null): always create — preserving AutoMapper's loud
    // duplicate detection for a genuinely duplicate repository registration.
    // Explicit endpoint path (skipPairs != null): skip a pair another profile declared, and skip a
    // pair this profile already created. Keys on the forward (entity → DTO) pair; a forward map's
    // ReverseMap target isn't enumerable at config-build time, so a standalone reverse-only user map
    // isn't deduped against (no such case in practice).
    private bool CanCreate(Type source, Type dest, ISet<(Type Source, Type Destination)>? skipPairs)
    {
        if (skipPairs is null)
            return true;

        if (skipPairs.Contains((source, dest)))
            return false;

        return _created.Add((source, dest));
    }
}

internal class ListOfShiftFileDtoToString : ITypeConverter<List<ShiftFileDTO>?, string?>
{
    public string? Convert(List<ShiftFileDTO>? source, string? destination, ResolutionContext context)
    {
        if (source == null)
            return null;

        return JsonSerializer.Serialize(source);
    }
}

internal class StringToListOfShiftFileDto : ITypeConverter<string?, List<ShiftFileDTO>?>
{
    public List<ShiftFileDTO>? Convert(string? source, List<ShiftFileDTO>? destination, ResolutionContext context)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return new List<ShiftFileDTO>();
        }

        return JsonSerializer.Deserialize<List<ShiftFileDTO>>(source) ?? new List<ShiftFileDTO>();
    }
}

internal class ShiftEntityToShiftEntitySelectDTO : ITypeConverter<ShiftEntityBase, ShiftEntitySelectDTO?>
{
    public ShiftEntitySelectDTO? Convert(ShiftEntityBase source, ShiftEntitySelectDTO? destination, ResolutionContext context)
    {
        string value = "";
        string? text = null;

        if (source != null)
        {
            var attribute = (ShiftEntityKeyAndNameAttribute?)Attribute.GetCustomAttribute(source.GetType(), typeof(ShiftEntityKeyAndNameAttribute));

            if (attribute is not null)
            {
                value = source.GetType().GetProperty(attribute.Value)?.GetValue(source)?.ToString() ?? string.Empty;

                text = source.GetType().GetProperty(attribute.Text)?.GetValue(source)?.ToString() ?? string.Empty;
            }

            return new ShiftEntitySelectDTO { Value = value, Text = text };
        }
        else
        {
            return null;
        }
    }
}