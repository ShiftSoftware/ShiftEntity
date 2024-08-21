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
            {
                var baseGenericArguments = repositoryType.BaseType.GetGenericArguments();

                var entity = baseGenericArguments
                    .Where(x => x.BaseType is not null)
                    .Where(x => x.BaseType!.IsAssignableTo(typeof(ShiftEntityBase)))
                    .FirstOrDefault();

                var listDto = baseGenericArguments
                    .Where(x => x.BaseType is not null)
                    .Where(x => x.BaseType!.IsAssignableTo(typeof(ShiftEntityListDTO)))
                    .FirstOrDefault();

                var viewAndUpsertDTOorMixedDTO = baseGenericArguments
                    .Where(x => x.BaseType is not null)
                    .Where(x => x.BaseType!.IsAssignableTo(typeof(ShiftEntityViewAndUpsertDTO)) || x.BaseType!.IsAssignableTo(typeof(ShiftEntityMixedDTO)))
                    .FirstOrDefault();


                if (listDto is not null)
                    CreateMap(entity, listDto);

                if (viewAndUpsertDTOorMixedDTO is not null)
                {
                    CreateMap(entity, viewAndUpsertDTOorMixedDTO)
                        .DefaultEntityToDtoAfterMap()
                        .ReverseMap()
                        .DefaultDtoToEntityAfterMap();
                }
            }
        }
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