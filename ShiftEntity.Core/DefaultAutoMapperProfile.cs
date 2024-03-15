using AutoMapper;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.ShiftEntity.Model;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ShiftSoftware.ShiftEntity.Core;

public class DefaultAutoMapperProfile : Profile
{
    public DefaultAutoMapperProfile()
    {
        CreateMap<List<ShiftFileDTO>?, string?>().ConvertUsing<ListOfShiftFileDtoToString>();
        CreateMap<string?, List<ShiftFileDTO>?>().ConvertUsing<StringToListOfShiftFileDto>();

        CreateMap<ShiftEntityBase, ShiftEntitySelectDTO?>().ConvertUsing<ShiftEntityToShiftEntitySelectDTO>();
        CreateMap<ShiftEntitySelectDTO, ShiftEntityBase>().ConstructUsing(x => null);

        var repositoryTypes = AppDomain
            .CurrentDomain
            .GetAssemblies()
            //The following shows up when calling GetTypes(). So I just excluded it by using the Name: Could not load type 'SqlGuidCaster' from assembly Microsoft.Data.SqlClient: https://github.com/dotnet/SqlClient/issues/1930
            .Where(x => !x.GetName().FullName.StartsWith("Microsoft.Data.SqlClient"))
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
                    CreateMap(entity, viewAndUpsertDTO)
                        .AfterMap((entity, dto) =>
                        {
                            foreach (var property in dto.GetType().GetProperties())
                            {
                                if (property.PropertyType == typeof(ShiftEntitySelectDTO))
                                {
                                    var selectDTO = (ShiftEntitySelectDTO?)property.GetValue(dto);

                                    if (selectDTO is null)
                                    {
                                        selectDTO = new ShiftEntitySelectDTO() { Value = "", Text = null };
                                    }

                                    if (selectDTO.Value == "" && selectDTO.Text is null)
                                    {
                                        string value = "";

                                        var foriegnKeyNameByConvention = $"{property.Name}ID";

                                        var foregnKeyPropertyByConvention = entity
                                        .GetType()
                                        .GetProperties()
                                        .FirstOrDefault(x => x.Name.Equals(foriegnKeyNameByConvention, StringComparison.InvariantCultureIgnoreCase));

                                        if (foregnKeyPropertyByConvention is not null)
                                        {
                                            value = foregnKeyPropertyByConvention.GetValue(entity)?.ToString() ?? "";
                                        }

                                        property.SetValue(dto, new ShiftEntitySelectDTO { Text = null, Value = value });
                                    }
                                }
                            }
                        })
                        .ReverseMap()
                        .AfterMap((dto, entity) =>
                        {
                            //foreach (var property in entity.GetType().GetProperties())
                            //{
                            //    if (property.PropertyType.IsAssignableTo(typeof(ShiftEntityBase)))
                            //    {
                            //        property.SetValue(dto, null);
                            //    }
                            //}

                            foreach (var property in dto.GetType().GetProperties())
                            {
                                if (property.PropertyType == typeof(ShiftEntitySelectDTO))
                                {
                                    var selectDTO = (ShiftEntitySelectDTO?)property.GetValue(dto);

                                    if (selectDTO is null)
                                    {
                                        selectDTO = new ShiftEntitySelectDTO() { Value = "", Text = null };
                                    }

                                    if (!string.IsNullOrWhiteSpace(selectDTO.Value))
                                    {
                                        var foriegnKeyNameByConvention = $"{property.Name}ID";

                                        var foregnKeyPropertyByConvention = entity
                                        .GetType()
                                        .GetProperties()
                                        .FirstOrDefault(x => x.Name.Equals(foriegnKeyNameByConvention, StringComparison.InvariantCultureIgnoreCase));

                                        if (foregnKeyPropertyByConvention is not null)
                                        {
                                            foregnKeyPropertyByConvention.SetValue(entity, long.Parse(selectDTO.Value));
                                        }
                                    }
                                }
                            }
                        })
                        .ForAllMembers(x => x.Condition((src, dest, value) =>
                        {
                            if (value != null && value.GetType().IsAssignableTo(typeof(ShiftEntityBase)))
                                return false;

                            return true;
                        }));

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