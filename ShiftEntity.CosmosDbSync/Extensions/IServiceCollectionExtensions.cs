using EntityFrameworkCore.Triggered;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ShiftSoftware.ShiftEntity.CosmosDbSync.Services;
using ShiftSoftware.ShiftEntity.CosmosDbSync.Triggers;

namespace ShiftSoftware.ShiftEntity.CosmosDbSync.Extensions;

public static class IServiceCollectionExtensions
{
    public static IServiceCollection AddShiftEntityCosmosDbSync(this IServiceCollection services, ShiftEntityCosmosDbOptions options)
    {
        //Register triggers
        services.AddTransient(typeof(IAfterSaveTrigger<>), typeof(SyncToCosmosDbAfterSaveTrigger<>));

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

        return services;
    }

    public static IServiceCollection AddShiftEntityCosmosDbSync(this IServiceCollection services, Action<ShiftEntityCosmosDbOptions> optionBuilder)
    {
        ShiftEntityCosmosDbOptions o = new();
        optionBuilder.Invoke(o);

        return services.AddShiftEntityCosmosDbSync(o);
    }
}
