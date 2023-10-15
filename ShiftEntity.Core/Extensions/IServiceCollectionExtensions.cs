
using Microsoft.Extensions.DependencyInjection.Extensions;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Services;
using System;
using System.Linq;
using System.Reflection;

namespace Microsoft.Extensions.DependencyInjection;

public static class IServiceCollectionExtensions
{
    public static IServiceCollection AddShiftEntity(this IServiceCollection services, Action<ShiftEntityOptions> shiftEntityOptionsBuilder)
    {
        ShiftEntityOptions o = new();

        shiftEntityOptionsBuilder.Invoke(o);

        return AddShiftEntity(services, o);
    }

    public static IServiceCollection RegisterIShiftEntityFind(this IServiceCollection services, Assembly? repositoriesAssembly = null)
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
                services.AddScoped(interfaceType, repositoryType);
            }
        }

        return services;
    }

    public static IServiceCollection AddShiftEntity(this IServiceCollection services, ShiftEntityOptions shiftEntityOptions)
    {
        services
            .TryAddSingleton(shiftEntityOptions);

        if (shiftEntityOptions.azureStorageOptions.Count > 0)
            services.TryAddSingleton(new AzureStorageService(shiftEntityOptions.azureStorageOptions));

        services.RegisterIShiftEntityFind(shiftEntityOptions.RepositoriesAssembly);

        services.AddAutoMapper(shiftEntityOptions.AutoMapperAssemblies);

        return services;
    }
}
