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
    /// when the transaction around it commits. A rolled-back save replicates nothing. Saves made under
    /// <see cref="ShiftDbContext.SuppressReplication"/> are not replicated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The context follows the transactions begun through its <c>Database</c>. Inside any other one (a
    /// <c>TransactionScope</c>, or one joined with <c>UseTransaction</c>) it is not told about the commit, so a save of
    /// replicated rows is refused there before it writes. Such a save runs under
    /// <see cref="ShiftDbContext.SuppressReplication"/>, and a catch-up replicates its rows.
    /// </para>
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
    /// <para>
    /// Both replication registrations used to switch the trigger library (EntityFrameworkCore.Triggered) on for the
    /// host's context. They no longer do, and the framework no longer references it. A host that still registers
    /// triggers is stopped when it starts, with a message that says how to convert them to save handlers.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddShiftEntityCosmosDbReplicationOnSave<TDbContext>(this IServiceCollection services, Action<ShiftEntityCosmosDbOptions> optionBuilder)
        where TDbContext : ShiftDbContext
    {
        services.AddSaveLifecycleHost();

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
        services.AddSaveLifecycleHost();
        services.AddScoped<CosmosDBReplication>();
        services.CheckReplicationRegistrationsAtStart();
        return services;
    }

    //Once, whichever registration comes first. See ReplicationRegistrationCheck.
    private static void CheckReplicationRegistrationsAtStart(this IServiceCollection services) =>
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ReplicationRegistrationCheck>());
}
