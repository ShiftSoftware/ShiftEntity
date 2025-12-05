using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using System;
using System.Linq;

namespace AutoMapper;

public static class AutoMapperExtensions
{
    private static void DefaultDtoToEntityAfterMapAction(object dto, object entity)
    {
        foreach (var property in dto.GetType().GetProperties())
        {
            if (property.PropertyType == typeof(ShiftEntitySelectDTO))
            {
                var selectDTO = (ShiftEntitySelectDTO?)property.GetValue(dto);

                if (selectDTO is null)
                {
                    selectDTO = new ShiftEntitySelectDTO();
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
    }

    private static bool Condition(object src, object dest, object value)
    {
        if (value != null && value.GetType().IsAssignableTo(typeof(ShiftEntityBase)))
            return false;

        return true;
    }


    public static IMappingExpression DefaultDtoToEntityAfterMap(this IMappingExpression mappingExpression)
    {
        mappingExpression
            .AfterMap((dto, entity) => DefaultDtoToEntityAfterMapAction(dto, entity))
            .ForAllMembers(x => x.Condition((src, dest, value) => Condition(src, dest, value)));

        return mappingExpression;
    }

    public static IMappingExpression<T1, T2> DefaultDtoToEntityAfterMap<T1, T2>(this IMappingExpression<T1, T2> mappingExpression)
    {
        mappingExpression
            .AfterMap((dto, entity) => DefaultDtoToEntityAfterMapAction(dto, entity))
            .ForAllMembers(x => x.Condition((src, dest, value) => Condition(src, dest, value)));

        return mappingExpression;
    }

    private static void DefaultEntityToDtoAfterMapAction(object entity, object dto)
    {
        foreach (var property in dto.GetType().GetProperties())
        {
            if (property.PropertyType == typeof(ShiftEntitySelectDTO))
            {
                var selectDTO = (ShiftEntitySelectDTO?)property.GetValue(dto);

                if (selectDTO is null)
                {
                    selectDTO = new ShiftEntitySelectDTO();
                }

                if (string.IsNullOrWhiteSpace(selectDTO.Value) && selectDTO.Text is null)
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

                    property.SetValue(dto, new ShiftEntitySelectDTO() { Value = value });
                }
            }
        }
    }

    public static IMappingExpression DefaultEntityToDtoAfterMap(this IMappingExpression mappingExpression)
    {
        mappingExpression.AfterMap((entity, dto, context) => DefaultEntityToDtoAfterMapAction(entity, dto));

        return mappingExpression;
    }

    public static IMappingExpression<T1, T2> DefaultEntityToDtoAfterMap<T1, T2>(this IMappingExpression<T1, T2> mappingExpression)
    {
        mappingExpression.AfterMap((entity, dto) => DefaultEntityToDtoAfterMapAction(entity, dto));

        return mappingExpression;
    }
}