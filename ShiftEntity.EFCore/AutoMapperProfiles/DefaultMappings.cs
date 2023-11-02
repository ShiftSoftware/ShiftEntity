
using AutoMapper;
using ShiftSoftware.ShiftEntity.Core;
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
            var attribute = (ShiftEntityValueTextAttribute?)Attribute.GetCustomAttribute(source.GetType(), typeof(ShiftEntityValueTextAttribute));

            if (attribute is not null)
            {
                value = source.GetType().GetProperty(attribute.Value)?.GetValue(source)?.ToString() ?? string.Empty;

                text = source.GetType().GetProperty(attribute.Text)?.GetValue(source)?.ToString() ?? string.Empty;
            }
        }

        return new ShiftEntitySelectDTO { Value = value, Text = text };
    }
}