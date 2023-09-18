using EntityFrameworkCore.Triggered;
using EntityFrameworkCore.Triggered.Transactions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.EFCore.Triggers;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Web.Services;
using ShiftSoftware.ShiftEntity.Web.Triggers;
using System;
using System.Linq;
using System.Reflection;
using Thinktecture;

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
        builder.Services.AddTransient(typeof(IBeforeSaveTrigger<>),typeof(GeneralTrigger<>));
        builder.Services.AddTransient(typeof(IBeforeSaveTrigger<>),typeof(SetUserAndCompanyInfoTrigger<>));
        builder.Services.AddTransient(typeof(IAfterSaveTrigger<>), typeof(ReloadAfterSaveTrigger<>));
        builder.Services.AddTransient(typeof(IBeforeCommitTrigger<>), typeof(BeforeCommitTrigger<>));

        return builder;
    }

    private static IMvcBuilder RegisterIShiftEntityFind(this IMvcBuilder builder, Assembly? repositoriesAssembly=null)
    {
        Assembly repositoryAssembly = repositoriesAssembly ?? Assembly.GetEntryAssembly()!; // Adjust this as needed

        // Find all types in the assembly that implement IRepository<>
        var repositoryTypes = repositoryAssembly!.GetTypes()
            .Where(t => t.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IShiftEntityFind<>)));

        // Register each IRepository<> implementation with its corresponding interface
        foreach (var repositoryType in repositoryTypes)
        {
            var interfaceType = repositoryType.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IShiftEntityFind<>));
            if (interfaceType != null)
            {
                builder.Services.AddScoped(interfaceType, repositoryType);
            }
        }

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
        builder.RegisterIShiftEntityFind(shiftEntityOptions.RepositoriesAssembly);

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
            };

            return new ConfigureNamedOptions<JsonOptions>(Options.DefaultName, options);
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
