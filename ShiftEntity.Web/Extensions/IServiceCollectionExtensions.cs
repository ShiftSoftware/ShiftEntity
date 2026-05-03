using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Web.Services;
using System.Text.Json.Serialization.Metadata;

namespace Microsoft.Extensions.DependencyInjection;

public static class IServiceCollectionExtensions
{
    /// <summary>
    /// Wires the DI-aware HashId <see cref="JsonTypeInfo"/> modifier into both AspNetCore JSON
    /// pipelines so identity-hashed properties (Brand/Country/User/...) serialize through
    /// <c>IHashIdService.GetHasherFor</c> at type-info build time, picking up the correct
    /// identity salt from <see cref="HashIdOptions"/> instead of the legacy
    /// attribute-construction-time path. Required for hosts that don't go through
    /// <c>AddShiftEntityWeb</c> (e.g. Azure Functions Worker with the AspNetCore extension);
    /// <c>AddShiftEntityWeb</c> calls this internally.
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
}
