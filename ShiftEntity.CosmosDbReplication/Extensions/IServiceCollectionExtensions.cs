using EntityFrameworkCore.Triggered;
using Microsoft.EntityFrameworkCore;
using ShiftSoftware.ShiftEntity.CosmosDbReplication;
using ShiftSoftware.ShiftEntity.CosmosDbReplication.Services;
using ShiftSoftware.ShiftEntity.CosmosDbReplication.Triggers;
using ShiftSoftware.ShiftEntity.EFCore;

namespace Microsoft.Extensions.DependencyInjection;

public static class IServiceCollectionExtensions
{
    public static IServiceCollection AddShiftEntityCosmosDbReplicationTrigger<TDbContext>(this IServiceCollection services, Action<ShiftEntityCosmosDbOptions> optionBuilder)
        where TDbContext : ShiftDbContext
    {
        services.AddDbContext<TDbContext>((sp, options) => options.UseTriggers());

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

    public static IServiceCollection AddShiftEntityCosmosDbReplication<TDbContext>(this IServiceCollection services)
        where TDbContext : ShiftDbContext
    {
        services.AddDbContext<TDbContext>((sp, options) => options.UseTriggers());
        services.AddScoped<CosmosDBReplication>();
        return services;
    }
}
