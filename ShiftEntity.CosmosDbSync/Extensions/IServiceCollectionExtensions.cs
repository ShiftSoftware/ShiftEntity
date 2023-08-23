using EntityFrameworkCore.Triggered;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.CosmosDbSync.Services;
using ShiftSoftware.ShiftEntity.CosmosDbSync.Triggers;
using System.Reflection;

namespace ShiftSoftware.ShiftEntity.CosmosDbSync.Extensions;

public static class IServiceCollectionExtensions
{
    private static IServiceCollection RegisterIShiftEntityPrepareForSync(this IServiceCollection service, Assembly? repositoriesAssembly=null)
    {
        Assembly repositoryAssembly = repositoriesAssembly ?? Assembly.GetEntryAssembly()!; // Adjust this as needed

        var repositoryTypes = repositoryAssembly.GetTypes()
            .Where(t => t.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IShiftEntityPrepareForSyncAsync<>)));

        // Register each IRepository<> implementation with its corresponding interface
        foreach (var repositoryType in repositoryTypes)
        {
            var interfaceType = repositoryType.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IShiftEntityPrepareForSyncAsync<>));
            if (interfaceType != null)
            {
                service.AddScoped(interfaceType, repositoryType);
            }
        }

        return service;
    }

    public static IServiceCollection AddShiftEntityCosmosDbSync(this IServiceCollection services, ShiftEntityCosmosDbOptions options)
    {
        //Register all IShiftEntityPrepareForSyncAsync
        services.RegisterIShiftEntityPrepareForSync(options.RepositoriesAssembly);

        //Register triggers
        services.AddTransient(typeof(IAfterSaveTrigger<>), typeof(SyncToCosmosDbAfterSaveTrigger<>));
        services.AddTransient(typeof(IBeforeSaveTrigger<>), typeof(PreventChangePartitionKeyValueTrigger<>));

        CosmosDBAccount connection = new();

        //register options

        //Add default connection and database
        if(options.ConnectionString is not null)
        {
            connection.ConnectionString = options.ConnectionString;
            connection.IsDefault=true;

            if (options.DefaultDatabaseName is not null)
            {
                connection.DefaultDatabaseName = options.DefaultDatabaseName;
            }

            options.Accounts.Add(connection);
        }

        //There must be only one default connection
        if (options.Accounts.Count(c => c.IsDefault) > 1)
            throw new ArgumentException("There must be only at least one default connection");

        //The account names must be unique
        if (options.Accounts.GroupBy(x=> x.Name?.ToLower()).Any(x=> x.Count()>1))
            throw new ArgumentException("There account names must be unique");

        services.TryAddSingleton(options);
        services.AddScoped(typeof(CosmosDBService<>));

        //Register DbContextProvider
        foreach (var provider in options.DbContextProviders)
        {
            services.AddSingleton<IDbContextProvider>(provider);
        }

        return services;
    }

    public static IServiceCollection AddShiftEntityCosmosDbSync(this IServiceCollection services, Action<ShiftEntityCosmosDbOptions> optionBuilder)
    {
        ShiftEntityCosmosDbOptions o = new();
        optionBuilder.Invoke(o);

        return services.AddShiftEntityCosmosDbSync(o);
    }
}
