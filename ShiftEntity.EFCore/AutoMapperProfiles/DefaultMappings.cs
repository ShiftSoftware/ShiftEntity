
using AutoMapper;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using System.Text.Json;

namespace ShiftSoftware.ShiftEntity.EFCore.AutoMapperProfiles;

public class DefaultMappings : Profile
{
    public DefaultMappings()
    {
        CreateMap<List<ShiftFileDTO>?, string?>().ConvertUsing<ListOfShiftFileDtoToString>();
        CreateMap<string?, List<ShiftFileDTO>?>().ConvertUsing<StringToListOfShiftFileDto>();

        CreateMap<ShiftEntityBase, ShiftEntitySelectDTO>().ConvertUsing<ShiftEntityToShiftEntitySelectDTO>();

        var repositoryTypes = AppDomain
            .CurrentDomain
            .GetAssemblies()
            .SelectMany(x => x.GetTypes())
            .Where(type => type.IsClass && !type.IsAbstract && typeof(ShiftRepositoryBase).IsAssignableFrom(type))
            .ToList();

        foreach (var repositoryType in repositoryTypes)
        {
            if (repositoryType.BaseType is not null)
            {
                var baseGenericArguments = repositoryType.BaseType.GetGenericArguments();

                var entity = baseGenericArguments.FirstOrDefault(x => x.BaseType is not null && x.BaseType.IsAssignableTo(typeof(ShiftEntityBase)));

                var listDto = baseGenericArguments.FirstOrDefault(x => x.BaseType is not null && x.BaseType.IsAssignableTo(typeof(ShiftEntityListDTO)));

                var viewAndUpsertDTO = baseGenericArguments.FirstOrDefault(x => x.BaseType is not null && x.BaseType.IsAssignableTo(typeof(ShiftEntityViewAndUpsertDTO)));

                var mixedDTO = baseGenericArguments.FirstOrDefault(x => x.BaseType is not null && x.BaseType.IsAssignableTo(typeof(ShiftEntityMixedDTO)));

                if (listDto is not null)
                    CreateMap(entity, listDto);

                if (viewAndUpsertDTO is not null)
                    CreateMap(entity, viewAndUpsertDTO).ReverseMap();

                if (mixedDTO is not null)
                    CreateMap(entity, mixedDTO).ReverseMap();
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

internal class ShiftEntityToShiftEntitySelectDTO : ITypeConverter<ShiftEntityBase, ShiftEntitySelectDTO>
{
    public ShiftEntitySelectDTO Convert(ShiftEntityBase source, ShiftEntitySelectDTO destination, ResolutionContext context)
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
        }

        return new ShiftEntitySelectDTO { Value = value, Text = text };
    }
}