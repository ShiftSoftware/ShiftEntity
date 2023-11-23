using EntityFrameworkCore.Triggered;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.CosmosDbReplication;
using ShiftSoftware.ShiftEntity.CosmosDbReplication.Services;
using ShiftSoftware.ShiftEntity.CosmosDbReplication.Triggers;
using ShiftSoftware.ShiftEntity.Model;
using System.Reflection;

namespace Microsoft.Extensions.DependencyInjection;

public static class IServiceCollectionExtensions
{
    public static IServiceCollection AddShiftEntityCosmosDbReplicationTrigger(this IServiceCollection services, ShiftEntityCosmosDbOptions options)
    {
        //Register triggers
        services.AddTransient(typeof(IAfterSaveTrigger<>), typeof(ReplicateToCosmosDbAfterSaveTrigger<>));
        services.AddTransient(typeof(IBeforeSaveTrigger<>), typeof(PreventChangePartitionKeyValueTrigger<>));
        services.AddTransient(typeof(IBeforeSaveTrigger<>), typeof(LogDeletedRowsTrigger<>));

        CosmosDBAccount connection = new();

        //register options

        //Add default connection and database
        if (options.ConnectionString is not null)
        {
            connection.ConnectionString = options.ConnectionString;
            connection.IsDefault = true;

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
        if (options.Accounts.GroupBy(x => x.Name?.ToLower()).Any(x => x.Count() > 1))
            throw new ArgumentException("There account names must be unique");

        services.TryAddSingleton(options);
        services.AddScoped(typeof(CosmosDBService<>));

        foreach (var shiftDbContext in options.ShiftDbContextStorage)
        {
            services.AddSingleton(
                new DbContextProvider(shiftDbContext.ShiftDbContextType, shiftDbContext.DbContextOptions));
        }

        return services;
    }

    public static IServiceCollection AddShiftEntityCosmosDbReplicationTrigger(this IServiceCollection services, Action<ShiftEntityCosmosDbOptions> optionBuilder)
    {
        ShiftEntityCosmosDbOptions o = new();
        optionBuilder.Invoke(o);

        return services.AddShiftEntityCosmosDbReplicationTrigger(o);
    }

    public static IServiceCollection AddShiftEntityCosmosDbReplication(this IServiceCollection services)
    {
        services.AddScoped<CosmosDBReplication>();
        return services;
    }
}
