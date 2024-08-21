using Microsoft.Extensions.DependencyInjection.Extensions;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Services;
using System;

namespace Microsoft.Extensions.DependencyInjection;

public static class IServiceCollectionExtensions
{
    public static IServiceCollection AddShiftEntity(this IServiceCollection services, Action<ShiftEntityOptions> shiftEntityOptionsBuilder)
    {
        ShiftEntityOptions o = new();

        shiftEntityOptionsBuilder.Invoke(o);

        return AddShiftEntity(services, o);
    }

    public static IServiceCollection AddShiftEntity(this IServiceCollection services, ShiftEntityOptions shiftEntityOptions)
    {
        services
            .TryAddSingleton(shiftEntityOptions);

        if (shiftEntityOptions.azureStorageOptions.Count > 0)
            services.TryAddSingleton(new AzureStorageService(shiftEntityOptions.azureStorageOptions));

        shiftEntityOptions.AutoMapperAssemblies.Insert(0, typeof(DefaultAutoMapperProfile).Assembly);

        services.AddAutoMapper(x =>
        {
            x.AddProfile(new DefaultAutoMapperProfile(shiftEntityOptions.DataAssemblies.ToArray()));
        }, shiftEntityOptions.AutoMapperAssemblies);

        return services;
    }
}
