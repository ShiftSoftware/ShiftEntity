using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Model;
using System;
using System.Linq;

namespace Microsoft.Extensions.DependencyInjection;

public static class IMvcBuilderExtensions
{
    /// <summary>
    /// Registers ShiftEntity Web services with the given configuration.
    /// Multiple calls to <c>services.Configure&lt;ShiftEntityOptions&gt;(...)</c> are additive.
    /// </summary>
    public static IMvcBuilder AddShiftEntityWeb(this IMvcBuilder builder, Action<ShiftEntityOptions> configure)
    {
        builder.Services.AddShiftEntity(configure);

        return AddShiftEntityWebCore(builder);
    }

    /// <summary>
    /// Registers ShiftEntity Web infrastructure without configuring options.
    /// Options can be registered separately via <c>services.Configure&lt;ShiftEntityOptions&gt;(o => { ... })</c>.
    /// </summary>
    public static IMvcBuilder AddShiftEntityWeb(this IMvcBuilder builder)
    {
        builder.Services.AddShiftEntity();

        return AddShiftEntityWebCore(builder);
    }

    private static IMvcBuilder AddShiftEntityWebCore(IMvcBuilder builder)
    {
        // Core registration runs first via AddShiftEntity (called by the public overloads above).
        // The shared web/functions plumbing (HashId JSON middleware, JsonOptions naming policy,
        // HTTP context, localization, identity/data-level providers) is applied through
        // AddShiftEntityWebSharedCore so the Functions Worker entry point reuses exactly the
        // same wiring. Everything that remains here is MVC-controller-pipeline specific.
        // OData middleware registration lives in the consumer's composition root (the
        // commented-out AddShiftEntityOdata sample further down shows the shape) since EDM
        // model construction is application-specific.

        builder.Services.AddShiftEntityWebSharedCore();

        // MVC-only: wraps [ApiController] model-state 400 responses in ShiftEntityResponse.
        // Functions Worker doesn't run through this pipeline, so this stays out of the shared
        // helper.
        builder.Services.AddSingleton<IConfigureOptions<ApiBehaviorOptions>>(sp =>
        {
            var shiftEntityOptions = sp.GetRequiredService<ShiftEntityOptions>();

            return new ConfigureNamedOptions<ApiBehaviorOptions>(Options.Options.DefaultName, options =>
            {
                if (shiftEntityOptions._WrapValidationErrorResponseWithShiftEntityResponse)
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
                }
            });
        });

        return builder;
    }
    //public static IMvcBuilder AddShiftEntityOdata(this IMvcBuilder builder, Action<ShiftEntityODataOptions> shiftEntityODataOptionsBuilder)
    //{
    //    ShiftEntityODataOptions o = new();
    //    shiftEntityODataOptionsBuilder.Invoke(o);

    //    return AddShiftEntityOdata(builder, o);
    //}
    //public static IMvcBuilder AddShiftEntityOdata(this IMvcBuilder builder, ShiftEntityODataOptions shiftEntityODataOptions)
    //{
    //    builder.Services.TryAddSingleton(shiftEntityODataOptions);

    //    shiftEntityODataOptions.GenerateEdmModel();

    //    //Configre OData
    //    builder.AddOData(oDataoptions =>
    //    {
    //        //oDataoptions.Count().Filter().Expand().Select().OrderBy().SetMaxTop(1000);

    //        if (shiftEntityODataOptions._Count)
    //            oDataoptions.Count();
    //        if (shiftEntityODataOptions._Filter)
    //            oDataoptions.Filter();
    //        if (shiftEntityODataOptions._Expand)
    //            oDataoptions.Expand();
    //        if (shiftEntityODataOptions._Select)
    //            oDataoptions.Select();
    //        if (shiftEntityODataOptions._OrderBy)
    //            oDataoptions.OrderBy();

    //        oDataoptions.SetMaxTop(shiftEntityODataOptions._MaxTop);

    //        oDataoptions.AddRouteComponents(shiftEntityODataOptions.RoutePrefix, shiftEntityODataOptions.EdmModel, serviceCollection =>
    //        {
    //            serviceCollection
    //            .AddHttpContextAccessor();
    //            //.TryAddSingleton<TimeZoneService>();

    //            serviceCollection.RegisterConverters();
    //        });
    //    });

    //    return builder;
    //}
}
