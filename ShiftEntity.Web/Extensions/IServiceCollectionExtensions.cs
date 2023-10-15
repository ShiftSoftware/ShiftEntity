using Microsoft.AspNetCore.OData.Formatter.Serialization;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.Web;

namespace Microsoft.Extensions.DependencyInjection;

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
