﻿using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Web.Services;
using System;
using System.Linq;

namespace ShiftSoftware.ShiftEntity.Web.Extensions;

public static class IMvcBuilderExtensions
{
    public static IMvcBuilder AddShiftEntity(this IMvcBuilder builder, Action<ShiftEntityOptions> shiftEntityOptionsBuilder)
    {
        ShiftEntityOptions o = new();

        shiftEntityOptionsBuilder.Invoke(o);

        return AddShiftEntity(builder, o);
    }
    public static IMvcBuilder AddShiftEntity(this IMvcBuilder builder, ShiftEntityOptions shiftEntityOptions)
    {
        builder.Services
            .AddHttpContextAccessor()
            .AddLocalization()
            .TryAddSingleton(shiftEntityOptions);

        builder.Services.TryAddSingleton<TimeZoneService>();

        builder.AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.PropertyNamingPolicy = null;
            options.RegisterTimeZoneConverters(builder.Services.BuildServiceProvider().GetRequiredService<TimeZoneService>());
        });

        if (shiftEntityOptions._WrapValidationErrorResponseWithShiftEntityResponse)
            builder.ConfigureApiBehaviorOptions(options =>
            {
                options.InvalidModelStateResponseFactory = context =>
                {
                    var errors = context.ModelState.Select(x => new { x.Key, x.Value?.Errors }).ToDictionary(x => x.Key, x => x.Errors);

                    var response = new ShiftEntityResponse<object>
                    {
                        Additional = errors.ToDictionary(x => x.Key, x => (object)x.Value?.Select(s => s.ErrorMessage)!)
                    };
                    return new BadRequestObjectResult(response);
                };
            });

        builder.Services.AddAutoMapper(shiftEntityOptions.AutoMapperAssemblies);

        return builder;
    }
    public static IMvcBuilder AddShiftEntityOdata(this IMvcBuilder builder, Action<ShiftEntityODataOptions> shiftEntityODataOptionsBuilder)
    {
        ShiftEntityODataOptions o = new();
        shiftEntityODataOptionsBuilder.Invoke(o);

        return AddShiftEntityOdata(builder, o);
    }
    public static IMvcBuilder AddShiftEntityOdata(this IMvcBuilder builder, ShiftEntityODataOptions shiftEntityODataOptions)
    {
        builder.Services.TryAddSingleton(shiftEntityODataOptions);

        shiftEntityODataOptions.GenerateEdmModel();

        //Configre OData
        builder.AddOData(oDataoptions =>
        {
            oDataoptions.Count().Filter().Expand().Select().OrderBy().SetMaxTop(1000);

            if (shiftEntityODataOptions._Count)
                oDataoptions.Count();
            if (shiftEntityODataOptions._Filter)
                oDataoptions.Filter();
            if (shiftEntityODataOptions._Expand)
                oDataoptions.Expand();
            if (shiftEntityODataOptions._Select)
                oDataoptions.Select();
            if (shiftEntityODataOptions._OrderBy)
                oDataoptions.OrderBy();

            oDataoptions.SetMaxTop(shiftEntityODataOptions._MaxTop);

            oDataoptions.AddRouteComponents(shiftEntityODataOptions.RoutePrefix, shiftEntityODataOptions.EdmModel, serviceCollection =>
            {
                serviceCollection
                .AddHttpContextAccessor()
                .TryAddSingleton<TimeZoneService>();

                serviceCollection.RegisterConverters();
            });
        });

        return builder;
    }
}