using EntityFrameworkCore.Triggered;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
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
        //Set IServiceCollection to options
        options.Services = services;

        //Register triggers
        services.AddTransient(typeof(IAfterSaveTrigger<>), typeof(ReplicateToCosmosDbAfterSaveTrigger<>));
        services.AddTransient(typeof(IBeforeSaveTrigger<>), typeof(PreventChangePartitionKeyValueTrigger<>));
        services.AddTransient(typeof(IBeforeSaveTrigger<>), typeof(LogDeletedRowsTrigger<>));

        return services;
    }

    public static IServiceCollection AddShiftEntityCosmosDbReplicationTrigger(this IServiceCollection services, Action<ShiftEntityCosmosDbOptions> optionBuilder)
    {
        ShiftEntityCosmosDbOptions o = new();

        //Set IServiceCollection to options
        o.Services = services;

        optionBuilder.Invoke(o);

        return services.AddShiftEntityCosmosDbReplicationTrigger(o);
    }

    public static IServiceCollection AddShiftEntityCosmosDbReplication(this IServiceCollection services)
    {
        services.AddScoped<CosmosDBReplication>();
        return services;
    }
}
