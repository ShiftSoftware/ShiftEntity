using EntityFrameworkCore.Triggered;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Services;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Web.Services;
using System;
using System.Linq;
using System.Text.Json.Serialization.Metadata;


namespace Microsoft.Extensions.DependencyInjection;

public static class IMvcBuilderExtensions
{
    /// <summary>
    /// Registers ShiftEntity Web services with the given configuration.
    /// Multiple calls to <c>services.Configure&lt;ShiftEntityOptions&gt;(...)</c> are additive.
    /// </summary>
    public static IMvcBuilder AddShiftEntityWeb(this IMvcBuilder builder, Action<ShiftEntityOptions> configure)
    {
        // Route through AddShiftEntity(configure) so the eager apply (which fires the static
        // HashId.Register*(...) side effects at registration time) runs for Web hosts too.
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
        builder.Services
            .AddHttpContextAccessor()
            .AddLocalization();

        // Wire the DI-aware HashId TypeInfoResolver modifier into both AspNetCore JSON pipelines
        // so identity hashers resolve through IHashIdService.GetHasherFor at type-info build time
        // (after DI is configured), fixing the attribute-construction-time timing race in the
        // legacy static path.
        builder.Services.AddShiftEntityHashIdJsonSupport();

        // MVC-specific JSON configuration — naming policy and Azure storage converters.
        builder.Services
            .AddOptions<JsonOptions>()
            .Configure<ShiftEntityOptions, AzureStorageService>(
                (o, shiftEntityOptions, azureStorageService) =>
                {
                    o.JsonSerializerOptions.PropertyNamingPolicy = shiftEntityOptions.JsonNamingPolicy;

                    if (shiftEntityOptions.azureStorageOptions.Count > 0)
                        o.RegisterAzureStorageServiceConverters(azureStorageService);
                });

        // Configure validation error wrapping from resolved ShiftEntityOptions
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

        builder.Services.AddScoped<IDefaultDataLevelAccess, DefaultDataLevelAccess>();
        builder.Services.AddScoped<IdentityClaimProvider>();
        builder.Services.AddScoped<ICurrentUserProvider, CurrentUserProvider>();

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
