using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ShiftSoftware.ShiftEntity.CosmosDbReplication;
using ShiftSoftware.ShiftEntity.CosmosDbReplication.Services;
using ShiftSoftware.ShiftEntity.EFCore;

namespace Microsoft.Extensions.DependencyInjection;

public static class IServiceCollectionExtensions
{
    /// <summary>
    /// Replicates the rows each save of <typeparamref name="TDbContext"/> writes, through the registrations
    /// <paramref name="optionBuilder"/> sets up. A sync starts once the save is committed: right after the save, or
    /// when the explicit, ambient or enlisted transaction around it commits. A rolled-back save replicates nothing.
    /// Saves made under <see cref="ShiftDbContext.SuppressReplication"/> are not replicated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <paramref name="optionBuilder"/> runs in each scope that replicates a save, and once when the host starts, in a
    /// scope of its own, so that a registration the framework refuses (<c>ReplicationRegistrationException</c>) stops
    /// the host rather than failing every replicated save. Any other failure there, such as a setting the environment
    /// lacks or a service only a request provides, is logged as a warning, and the builder runs again at the first
    /// replicated save.
    /// </para>
    /// <para>
    /// A pooled <typeparamref name="TDbContext"/> (<c>AddDbContextPool</c>, <c>AddPooledDbContextFactory</c>) is not
    /// supported: the pool builds the context's options once, so a save resolves the services its replication needs
    /// from the root provider, and the registrations and preparers are scoped services.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddShiftEntityCosmosDbReplicationTrigger<TDbContext>(this IServiceCollection services, Action<ShiftEntityCosmosDbOptions> optionBuilder)
        where TDbContext : ShiftDbContext
    {
        services.UseTriggersOn<TDbContext>();
        services.ConfigureDbContext<TDbContext>((sp, options) => options.UseShiftEntityAfterCommit(), ServiceLifetime.Singleton);

        // One observer however many contexts register replication; it replicates nothing for a type without a
        // registration.
        services.TryAddEnumerable(ServiceDescriptor.Transient<IShiftDbContextSaveObserver, CosmosDbReplicationSaveObserver>());
        services.AddScoped(x =>
        {
            ShiftEntityCosmosDbOptions o = new(x);
            optionBuilder.Invoke(o);
            return o;
        });
        services.CheckReplicationRegistrationsAtStart();

        return services;
    }

    public static IServiceCollection AddShiftEntityCosmosDbReplication<TDbContext>(this IServiceCollection services)
        where TDbContext : ShiftDbContext
    {
        services.UseTriggersOn<TDbContext>();
        services.AddScoped<CosmosDBReplication>();
        services.CheckReplicationRegistrationsAtStart();
        return services;
    }

    //Once, whichever registration comes first. See ReplicationRegistrationCheck.
    private static void CheckReplicationRegistrationsAtStart(this IServiceCollection services) =>
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ReplicationRegistrationCheck>());

    //Replication no longer uses the trigger library, but both registrations still switch it on for the host's context
    //until the cleanup release removes the library from the framework: host triggers registered through DI have relied
    //on this to fire. Configured, never registered: the host registers the context itself (AddDbContext, a pool or a
    //factory) with its provider and lifetimes, and a second AddDbContext here took those over when it came first.
    //Singleton, so a pooled context's singleton options can use it too.
    private static void UseTriggersOn<TDbContext>(this IServiceCollection services)
        where TDbContext : ShiftDbContext
    {
        services.ConfigureDbContext<TDbContext>((sp, options) => options.UseTriggers(), ServiceLifetime.Singleton);
    }
}
