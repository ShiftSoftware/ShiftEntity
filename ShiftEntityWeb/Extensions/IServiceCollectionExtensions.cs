using Microsoft.AspNetCore.OData.Formatter.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ShiftSoftware.ShiftEntity.Web.Services;

namespace ShiftSoftware.ShiftEntity.Web.Extensions;

public static class IServiceCollectionExtensions
{
    public static IServiceCollection RegisterTimeZoneConverters(this IServiceCollection services)
    {
        services.TryAddSingleton<IODataSerializerProvider>(serviceProvider =>
        {
            return new ODataDatetimeSerializerProvider(serviceProvider);
        });

        return services;
    }

    public static void RegisterOdataHashIdConverter(this IServiceCollection services)
    {
        services.AddSingleton<IODataSerializerProvider>(serviceProvider =>
        {
            return new ODataIDSerializerProvider(serviceProvider);
        });
    }
}
