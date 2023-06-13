using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyModel;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Model.HashId;
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

        o.ODatat.GenerateEdmModel();

        builder.Services.AddHttpContextAccessor()
            .AddLocalization()
            .TryAddSingleton(o);
        
        builder.Services.TryAddSingleton<TimeZoneService>();

        builder.AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.PropertyNamingPolicy = null;
            options.RegisterTimeZoneConverters(builder.Services.BuildServiceProvider().GetRequiredService<TimeZoneService>());
        });

        //Wrap the validation error with ShiftEntityResponse
        if (o._WrapValidationErrorResponseWithShiftEntityResponse)
            builder.ConfigureApiBehaviorOptions(options =>
            {
                options.InvalidModelStateResponseFactory = context =>
                {
                    var errors = context.ModelState.Select(x => new { x.Key, x.Value?.Errors })
                        .ToDictionary(x => x.Key, x => x.Errors);
                    var response = new ShiftEntityResponse<object>
                    {
                        Additional = errors.ToDictionary(x => x.Key, x => (object)x.Value?.Select(s => s.ErrorMessage)!)
                    };
                    return new BadRequestObjectResult(response);
                };
            });

        //Configre OData
        builder.AddOData(options =>
        {
            options.Count().Filter().Expand().Select().OrderBy().SetMaxTop(1000);

            if (o.ODatat._Count)
                options.Count();
            if (o.ODatat._Filter)
                options.Filter();
            if (o.ODatat._Expand)
                options.Expand();
            if (o.ODatat._Select)
                options.Select();
            if (o.ODatat._OrderBy)
                options.OrderBy();
            options.SetMaxTop(o.ODatat._MaxTop);

            options.AddRouteComponents(o.ODatat.RoutePrefix, o.ODatat.EdmModel, serviceCollection =>
            {
                serviceCollection.AddHttpContextAccessor()
                    .TryAddSingleton<TimeZoneService>();
                serviceCollection.RegisterConverters();
            });
        });

        return builder;
    }
}
