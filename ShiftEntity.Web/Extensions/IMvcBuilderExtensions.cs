using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Services;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Web.Services;
using System;
using System.Linq;
using Thinktecture;
using ShiftSoftware.ShiftEntity.Web.Triggers;
using EntityFrameworkCore.Triggered;
using ShiftSoftware.ShiftEntity.Web;


namespace Microsoft.Extensions.DependencyInjection;

public static class IMvcBuilderExtensions
{
    static bool locked = false;
    public static IMvcBuilder AddShiftEntityWeb(this IMvcBuilder builder, Action<ShiftEntityOptions> shiftEntityOptionsBuilder)
    {
        ShiftEntityOptions o = new();

        shiftEntityOptionsBuilder.Invoke(o);

        return AddShiftEntityWeb(builder, o);
    }

    public static IMvcBuilder AddShiftEntityWeb(this IMvcBuilder builder, ShiftEntityOptions shiftEntityOptions)
    {
        shiftEntityOptions.AutoMapperAssemblies.Add(typeof(ShiftSoftware.ShiftEntity.EFCore.AutoMapperProfiles.DefaultMappings).Assembly);
        builder.Services
            .AddHttpContextAccessor()
            .AddLocalization()
            .AddShiftEntity(shiftEntityOptions);

        builder.Services.TryAddSingleton<TimeZoneService>();

        builder.Services.RegisterShiftEntityEfCoreTriggers();
        builder.Services.AddTransient(typeof(IBeforeSaveTrigger<>), typeof(SetUserAndCompanyInfoTrigger<>));

        //Add rou number capability to sqlserver
        builder.Services.Decorate<DbContextOptions>((inner, provider) =>
        {
            var dbContextBuilder = new DbContextOptionsBuilder(inner);
            var slqServerOptionBuilder= new SqlServerDbContextOptionsBuilder(dbContextBuilder);

            slqServerOptionBuilder.AddRowNumberSupport();

            return dbContextBuilder.Options;
        });

        //Register timezone service to json options
        builder.Services.AddSingleton<IConfigureOptions<JsonOptions>>(p =>
        {
            Action<JsonOptions> options = (o) =>
            {
                o.JsonSerializerOptions.PropertyNamingPolicy = null;
                o.RegisterTimeZoneConverters(p.GetRequiredService<TimeZoneService>());

                if (shiftEntityOptions.azureStorageOptions.Count > 0)
                    o.RegisterAzureStorageServiceConverters(p.GetService<AzureStorageService>());
            };

            return new ConfigureNamedOptions<JsonOptions>(Options.Options.DefaultName, options);
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
            //oDataoptions.Count().Filter().Expand().Select().OrderBy().SetMaxTop(1000);

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
