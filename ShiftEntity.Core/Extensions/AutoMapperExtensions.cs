using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using System;
using System.Linq;

namespace AutoMapper;

public static class AutoMapperExtensions
{
    public static IMappingExpression DefaultDtoToEntityAfterMap(this IMappingExpression mappingExpression)
    {
        mappingExpression.AfterMap((dto, entity) =>
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
        })
        .ForAllMembers(x => x.Condition((src, dest, value) =>
        {
            if (value != null && value.GetType().IsAssignableTo(typeof(ShiftEntityBase)))
                return false;

            return true;
        }));

        return mappingExpression;
    }

    public static IMappingExpression<T1, T2> DefaultDtoToEntityAfterMap<T1, T2>(this IMappingExpression<T1, T2> mappingExpression)
    {
        mappingExpression.AfterMap((dto, entity) =>
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
        }).ForAllMembers(x => x.Condition((src, dest, value) =>
        {
            if (value != null && value.GetType().IsAssignableTo(typeof(ShiftEntityBase)))
                return false;

            return true;
        }));

        return mappingExpression;
    }

    public static IMappingExpression DefaultEntityToDtoAfterMap(this IMappingExpression mappingExpression)
    {
        mappingExpression.AfterMap((entity, dto) =>
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
        });

        return mappingExpression;
    }

    public static IMappingExpression<T1, T2> DefaultEntityToDtoAfterMap<T1, T2>(this IMappingExpression<T1, T2> mappingExpression)
    {
        mappingExpression.AfterMap((entity, dto) =>
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
        });

        return mappingExpression;
    }
}