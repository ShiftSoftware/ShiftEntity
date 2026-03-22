using ShiftSoftware.ShiftEntity.Core;
using System.Reflection;

namespace Microsoft.Extensions.DependencyInjection;

public static class IServiceCollectionExtensions
{

    private static IServiceCollection RegisterIShiftEntityFind(this IServiceCollection services, Assembly repositoriesAssembly)
    {
        Assembly repositoryAssembly = repositoriesAssembly ?? Assembly.GetEntryAssembly()!; // Adjust this as needed

        // Find all types in the assembly that implement IRepository<>
        var repositoryTypes = repositoryAssembly!.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract)
            .Where(t => t.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IShiftEntityFind<>)) &&
                !t.IsInterface);

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

    private static IServiceCollection RegisterIShiftEntityPrepareForReplication(this IServiceCollection services, Assembly repositoriesAssembly)
    {
        Assembly repositoryAssembly = repositoriesAssembly ?? Assembly.GetEntryAssembly()!; // Adjust this as needed

        var repositoryTypes = repositoryAssembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract)
            .Where(t => t.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IShiftEntityPrepareForReplicationAsync<>)) &&
                !t.IsInterface);

        // Register each IRepository<> implementation with its corresponding interface
        foreach (var repositoryType in repositoryTypes)
        {
            var interfaceType = repositoryType.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IShiftEntityPrepareForReplicationAsync<>));
            if (interfaceType != null)
            {
                services.AddScoped(interfaceType, repositoryType);
            }
        }

        return services;
    }

    public static IServiceCollection RegisterShiftRepositories(this IServiceCollection services, params Assembly[] assemblies)
    {
        foreach (var assembly in assemblies)
        {
            services.RegisterIShiftEntityFind(assembly);
            services.RegisterIShiftEntityPrepareForReplication(assembly);

            var repositoryTypes = assembly.GetTypes()
           .Where(type => type.IsClass && !type.IsAbstract && typeof(ShiftRepositoryBase).IsAssignableFrom(type))
           .ToList();

            foreach (var type in repositoryTypes)
            {
                services.AddScoped(type);
            }
        }

        if (assemblies is null || assemblies?.Count() < 1)
        {
            services.RegisterIShiftEntityFind(Assembly.GetEntryAssembly()!);
            services.RegisterIShiftEntityPrepareForReplication(Assembly.GetEntryAssembly()!);
        }

        return services;
    }
}
