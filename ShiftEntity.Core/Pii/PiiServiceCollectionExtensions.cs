using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ShiftSoftware.ShiftEntity.Core.Pii;

public static class PiiServiceCollectionExtensions
{
    public static IServiceCollection AddShiftEntityPii(this IServiceCollection services, Action<PiiOptions>? configure = null)
    {
        services.AddOptions<PiiOptions>();
        if (configure is not null)
            services.Configure(configure);
        services.TryAddSingleton<IPiiMasker, DefaultPiiMasker>();
        services.TryAddSingleton<PiiFieldProtection>();
        services.TryAddSingleton<PiiDtoProtector>();
        return services;
    }
}
