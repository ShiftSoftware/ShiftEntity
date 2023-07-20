﻿using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Web.Services;
using ShiftSoftware.ShiftEntity.Web.Triggers;
using System;
using System.Linq;

namespace ShiftSoftware.ShiftEntity.Web.Extensions;

public static class IMvcBuilderExtensions
{
    static bool locked = false;
    public static IMvcBuilder AddShiftEntity(this IMvcBuilder builder, Action<ShiftEntityOptions> shiftEntityOptionsBuilder)
    {
        ShiftEntityOptions o = new();

        shiftEntityOptionsBuilder.Invoke(o);

        return AddShiftEntity(builder, o);
    }

    private static IMvcBuilder RegisterTriggers(this IMvcBuilder builder)
    {
        builder.Services.Decorate<DbContextOptions>((inner, provider) =>
        {
            var dbContextBuilder = new DbContextOptionsBuilder(inner);
            dbContextBuilder.UseTriggers(t => t.AddTrigger<SetUserIdTrigger>());
            return dbContextBuilder.Options;
        });

        return builder;
    }

    public static IMvcBuilder AddShiftEntity(this IMvcBuilder builder, ShiftEntityOptions shiftEntityOptions)
    {
        builder.Services
            .AddHttpContextAccessor()
            .AddLocalization()
            .TryAddSingleton(shiftEntityOptions);
        builder.Services.TryAddSingleton<TimeZoneService>();

        builder.RegisterTriggers();

        //builder.Services.AddSingleton<IConfigureOptions<JsonOptions>>(p =>
        //{
        //    Action<JsonOptions> options = (o) =>
        //    {
        //        o.JsonSerializerOptions.PropertyNamingPolicy = null;
        //        o.RegisterTimeZoneConverters(p.GetRequiredService<TimeZoneService>());
        //    };

        //    return new ConfigureNamedOptions<JsonOptions>(Options.DefaultName, options);
        //});

        builder.Services.AddSingleton<JsonOptions>(p =>
        {
            var options = new JsonOptions();
           
            options.JsonSerializerOptions.PropertyNamingPolicy = null;
            options.RegisterTimeZoneConverters(p.GetRequiredService<TimeZoneService>());
            
            return options;
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
