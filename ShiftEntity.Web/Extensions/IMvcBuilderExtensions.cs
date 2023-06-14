using Microsoft.AspNetCore.Mvc;
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
    public static IMvcBuilder AddShiftEntity(this IMvcBuilder builder, Action<ShiftEntityOptions> options)
    {
        ShiftEntityOptions o = new();
        options.Invoke(o);

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

        //Wrap the validation error with ShiftEntityResponse
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

        return builder;
    }
    public static IMvcBuilder AddOdata(this IMvcBuilder builder, ShiftEntityOptions shiftEntityOptions)
    {
        shiftEntityOptions.ODataOptions.GenerateEdmModel();

        //Configre OData
        builder.AddOData(oDataoptions =>
        {
            oDataoptions.Count().Filter().Expand().Select().OrderBy().SetMaxTop(1000);

            if (shiftEntityOptions.ODataOptions._Count)
                oDataoptions.Count();
            if (shiftEntityOptions.ODataOptions._Filter)
                oDataoptions.Filter();
            if (shiftEntityOptions.ODataOptions._Expand)
                oDataoptions.Expand();
            if (shiftEntityOptions.ODataOptions._Select)
                oDataoptions.Select();
            if (shiftEntityOptions.ODataOptions._OrderBy)
                oDataoptions.OrderBy();

            oDataoptions.SetMaxTop(shiftEntityOptions.ODataOptions._MaxTop);

            oDataoptions.AddRouteComponents(shiftEntityOptions.ODataOptions.RoutePrefix, shiftEntityOptions.ODataOptions.EdmModel, serviceCollection =>
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
