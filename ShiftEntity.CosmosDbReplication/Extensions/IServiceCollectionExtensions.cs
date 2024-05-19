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
    public static IServiceCollection AddShiftEntityCosmosDbReplicationTrigger(this IServiceCollection services, Action<ShiftEntityCosmosDbOptions> optionBuilder)
    {
        

        services.AddTransient(typeof(IAfterSaveTrigger<>), typeof(ReplicateToCosmosDbAfterSaveTrigger<>));
        services.AddTransient(typeof(IBeforeSaveTrigger<>), typeof(PreventChangePartitionKeyValueTrigger<>));
        services.AddTransient(typeof(IBeforeSaveTrigger<>), typeof(LogDeletedRowsTrigger<>));
        services.AddScoped(x =>
        {
            ShiftEntityCosmosDbOptions o = new(x);
            optionBuilder.Invoke(o);
            return o;
        });

        return services;
    }

    public static IServiceCollection AddShiftEntityCosmosDbReplication(this IServiceCollection services)
    {
        services.AddScoped<CosmosDBReplication>();
        return services;
    }
}
