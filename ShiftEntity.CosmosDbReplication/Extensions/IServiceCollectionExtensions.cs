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
        services.UseTriggersOn<TDbContext>();

        services.AddTransient(typeof(IAfterSaveTrigger<>), typeof(ReplicateToCosmosDbAfterSaveTrigger<>));
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
        services.UseTriggersOn<TDbContext>();
        services.AddScoped<CosmosDBReplication>();
        return services;
    }

    //Both sides need the triggers pipeline on the host's context: the after-save trigger to fire, and the catch-up for
    //SaveChangesWithoutTriggersAsync. Configured, never registered: the host registers the context itself (AddDbContext,
    //a pool or a factory) with its provider and lifetimes, and a second AddDbContext here took those over when it came
    //first. Singleton, so a pooled context's singleton options can use it too.
    private static void UseTriggersOn<TDbContext>(this IServiceCollection services)
        where TDbContext : ShiftDbContext
    {
        services.ConfigureDbContext<TDbContext>((sp, options) => options.UseTriggers(), ServiceLifetime.Singleton);
    }
}
