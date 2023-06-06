using Microsoft.AspNetCore.OData.Formatter.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ShiftSoftware.ShiftEntity.Web.Services;

namespace ShiftSoftware.ShiftEntity.Web.Extensions;

public static class IServiceCollectionExtensions
{
    public static IServiceCollection RegisterConverters(this IServiceCollection services)
    {
        services.AddSingleton<IODataSerializerProvider>(serviceProvider =>
        {
            return new ShiftEntityODataSerializerProvider(serviceProvider);
        });

        return services;
    }
}
