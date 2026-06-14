using Microsoft.AspNetCore.Mvc;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.HashIds;
using ShiftSoftware.ShiftEntity.Core.Services;
using ShiftSoftware.ShiftEntity.Web.Services;
using System;
using System.Text.Json.Serialization.Metadata;

namespace Microsoft.Extensions.DependencyInjection;

public static class IServiceCollectionExtensions
{
    /// <summary>
    /// Wires the DI-aware HashId <see cref="JsonTypeInfo"/> modifier into both AspNetCore JSON
    /// pipelines so identity-hashed properties (Brand/Country/User/...) serialize through
    /// <c>IHashIdService.GetHasherFor</c> at type-info build time, picking up the correct
    /// identity salt from <see cref="HashIdOptions"/> instead of the legacy
    /// attribute-construction-time path. <c>AddShiftEntityWeb</c> and
    /// <c>AddShiftEntityFunctions</c> both call this internally; only call it directly from
    /// custom hosts that don't go through either entry point.
    /// </summary>
    public static IServiceCollection AddShiftEntityHashIdJsonSupport(this IServiceCollection services)
    {
        // MVC pipeline — used by controllers and OkObjectResult / ObjectResult execution. Functions
        // Worker's AspNetCore HTTP integration routes IActionResult responses through this path.
        services
            .AddOptions<Microsoft.AspNetCore.Mvc.JsonOptions>()
            .Configure<IHashIdService>((o, hashIdService) =>
            {
                var baseResolver = o.JsonSerializerOptions.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver();
                o.JsonSerializerOptions.TypeInfoResolver = baseResolver
                    .WithAddedModifier(HashIdJsonTypeInfoResolverModifier.Create(hashIdService));
            });

        // Minimal API / TypedResults pipeline.
        services
            .AddOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>()
            .Configure<IHashIdService>((o, hashIdService) =>
            {
                var baseResolver = o.SerializerOptions.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver();
                o.SerializerOptions.TypeInfoResolver = baseResolver
                    .WithAddedModifier(HashIdJsonTypeInfoResolverModifier.Create(hashIdService));
            });

        return services;
    }

    /// <summary>
    /// Registers ShiftEntity services for an Azure Functions Worker (Isolated) host that uses
    /// the AspNetCore extension (<c>ConfigureFunctionsWebApplication</c>). Wires the same
    /// HashId JSON middleware, JSON naming policy, Azure Storage converters and HTTP/identity
    /// plumbing as <c>AddShiftEntityWeb</c>, but skips the MVC controller pipeline pieces
    /// (<see cref="ApiBehaviorOptions"/> / <c>[ApiController]</c> validation factory) that
    /// have no effect outside MVC.
    /// </summary>
    public static IServiceCollection AddShiftEntityFunctions(this IServiceCollection services, Action<ShiftEntityOptions> configure)
    {
        services.AddShiftEntity(configure);

        return AddShiftEntityWebSharedCore(services);
    }

    /// <summary>
    /// Registers ShiftEntity Functions infrastructure without configuring options.
    /// Options can be registered separately via <c>services.Configure&lt;ShiftEntityOptions&gt;(o => { ... })</c>.
    /// </summary>
    public static IServiceCollection AddShiftEntityFunctions(this IServiceCollection services)
    {
        services.AddShiftEntity();

        return AddShiftEntityWebSharedCore(services);
    }

    /// <summary>
    /// Shared registrations used by both <c>AddShiftEntityWeb</c> (MVC) and
    /// <c>AddShiftEntityFunctions</c> (Functions Worker AspNetCore). Caller is responsible for
    /// running the core <c>AddShiftEntity</c> step before this — both entry points do so.
    /// </summary>
    internal static IServiceCollection AddShiftEntityWebSharedCore(this IServiceCollection services)
    {
        services
            .AddHttpContextAccessor()
            .AddLocalization();

        // Wire the DI-aware HashId TypeInfoResolver modifier into both AspNetCore JSON pipelines
        // so identity hashers resolve through IHashIdService.GetHasherFor at type-info build time
        // (after DI is configured).
        services.AddShiftEntityHashIdJsonSupport();

        // MVC JsonOptions — naming policy and Azure storage converters. Functions Worker's
        // AspNetCore HTTP integration routes IActionResult responses through this same options
        // bag, so this stays relevant in the Functions path.
        services
            .AddOptions<Microsoft.AspNetCore.Mvc.JsonOptions>()
            .Configure<ShiftEntityOptions, AzureStorageService>(
                (o, shiftEntityOptions, azureStorageService) =>
                {
                    o.JsonSerializerOptions.PropertyNamingPolicy = shiftEntityOptions.JsonNamingPolicy;

                    if (shiftEntityOptions.azureStorageOptions.Count > 0)
                        o.RegisterAzureStorageServiceConverters(azureStorageService);
                });

        services.AddScoped<IDefaultDataLevelAccess, DefaultDataLevelAccess>();
        services.AddScoped<IdentityClaimProvider>();
        services.AddScoped<ICurrentUserProvider, CurrentUserProvider>();

        // v2 data-level-access engine (scoped IAccessibleItemsSource + DataLevelAccessContext) — registered
        // alongside the legacy IDefaultDataLevelAccess; ShiftRepository starts consuming it in Phase 3.
        services.AddShiftEntityDataLevelAccess();

        return services;
    }
}
