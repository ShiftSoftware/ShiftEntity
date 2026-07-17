using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Attention;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.EFCore.Attention;
using ShiftSoftware.TypeAuth.Core.Actions;
using System.Reflection;

namespace Microsoft.Extensions.DependencyInjection;

public static class IServiceCollectionExtensions
{
    /// <summary>
    /// Registers the attention emission dispatcher: an in-memory channel
    /// (<see cref="IAttentionDispatcher"/>) plus the background service that drains it and
    /// invokes the registered <see cref="IAttentionConsumer"/>s. Once registered, every
    /// committed save that raises attention signals publishes one
    /// <see cref="AttentionRaised"/> event per newly-raised signal. Apps that never call
    /// this (or <see cref="AddAttentionConsumer{TConsumer}"/>) get no dispatcher in their
    /// graph and the save pipeline skips publishing entirely.
    /// </summary>
    /// <remarks>
    /// Idempotent — safe to call multiple times. To substitute a custom transport, register
    /// your own <see cref="IAttentionDispatcher"/> instead of calling this; the save
    /// pipeline publishes through whatever implementation is registered.
    /// </remarks>
    public static IServiceCollection AddAttentionEmission(this IServiceCollection services)
    {
        services.TryAddSingleton<ChannelAttentionDispatcher>();
        services.TryAddSingleton<IAttentionDispatcher>(sp => sp.GetRequiredService<ChannelAttentionDispatcher>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, AttentionDispatcherService>());

        return services;
    }

    /// <summary>
    /// Registers an <see cref="IAttentionConsumer"/> (scoped — resolved in a fresh DI scope
    /// per event) and ensures the emission dispatcher is registered, so a single call is
    /// enough to start receiving <see cref="AttentionRaised"/> events. Register as many
    /// consumers as needed; all of them receive every event.
    /// </summary>
    /// <remarks>
    /// Idempotent per consumer type — registering the same <typeparamref name="TConsumer"/>
    /// twice keeps a single registration.
    /// </remarks>
    public static IServiceCollection AddAttentionConsumer<TConsumer>(this IServiceCollection services)
        where TConsumer : class, IAttentionConsumer
    {
        services.AddAttentionEmission();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IAttentionConsumer, TConsumer>());

        return services;
    }

    public static IServiceCollection AddAttentionEvaluator<TEntity, TEvaluator>(this IServiceCollection services)
        where TEntity : class
        where TEvaluator : class, IAttentionEvaluator<TEntity>
    {
        services.AddScoped<TEvaluator>();

        var requiresHistory = typeof(IRequiresAttentionHistory<TEntity>).IsAssignableFrom(typeof(TEvaluator));

        services.AddSingleton(new AttentionEvaluatorDescriptor
        {
            TargetType = typeof(TEntity),
            EvaluatorTypeName = typeof(TEvaluator).Name,
            RequiresHistory = requiresHistory,
            Invoke = (sp, entity, original, action) =>
            {
                var evaluator = sp.GetRequiredService<TEvaluator>();
                var context = new AttentionContext<TEntity>
                {
                    Action = action,
                    Entity = (TEntity)entity,
                    Original = original as TEntity,
                    Services = sp,
                };
                return evaluator.Evaluate(context);
            },
            InvokeWithHistory = requiresHistory
                ? (sp, entity, original, action, history) =>
                {
                    var evaluator = (IRequiresAttentionHistory<TEntity>)sp.GetRequiredService<TEvaluator>();
                    var context = new AttentionContext<TEntity>
                    {
                        Action = action,
                        Entity = (TEntity)entity,
                        Original = original as TEntity,
                        Services = sp,
                    };
                    return evaluator.EvaluateWithHistory(context, history);
                }
                : null,
        });

        return services;
    }


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

        var existingMap = services.FirstOrDefault(d => d.ServiceType == typeof(ShiftEntityDtoMap));
        var map = existingMap?.ImplementationInstance as ShiftEntityDtoMap ?? new ShiftEntityDtoMap();

        map.PopulateFromAssemblies(assemblies ?? [Assembly.GetEntryAssembly()!]);

        if (existingMap is null)
            services.AddSingleton(map);

        // Attribute-driven endpoints: an entity decorated with [ShiftEntityEndpoint<…>] /
        // [ShiftEntitySecureEndpoint<…>] needs the built-in repository (registered just below), its default
        // AutoMapper map, and a DTO-map entry. Wire that here off the same assemblies — so the programmer makes
        // no extra service call — and map the routes in the pipeline with app.MapShiftEntityEndpoints<DB>().
        var endpointSpecs = ShiftEntityEndpointDiscovery.Discover(assemblies ?? [Assembly.GetEntryAssembly()!]);

        // The entity → TypeAuth action registry. Registered here even when it stays empty, so
        // consumers (for example the standalone attention endpoints) can resolve it and treat
        // a missing entry as "no action known for this type".
        var actionMap = GetOrAddShiftEntityActionMap(services);

        // The built-in repository, registered as an open generic — UNCONDITIONALLY, so it is available to
        // resolve ShiftRepository<DB, Entity, ListDTO, ViewDTO> for ANY entity: the attribute-driven endpoints
        // handled below, but also any programmer who simply wants to inject the built-in repository without
        // writing a repository subclass. It carries no per-entity state, so registering it once serves them all;
        // it is harmless when nothing resolves it and idempotent (TryAdd). No DB type is needed here — the
        // concrete DB is supplied by the closed repository type at resolution / map time.
        services.TryAddScoped(typeof(ShiftRepository<,,,>));

        if (endpointSpecs.Count > 0)
        {
            foreach (var spec in endpointSpecs)
            {
                map.Register(spec.Entity.Name, spec.ViewDto);

                // Secure attribute endpoints name their action (ActionTreeType + ActionName).
                // Resolve it once here and feed the action registry, so cross-entity surfaces
                // can apply the same permission check as the entity's own endpoints.
                if (spec.Secure)
                    actionMap.Register(spec.Entity.Name, ShiftEntityEndpointActionResolver.ResolveAction(spec.ActionTreeType!, spec.ActionName!));

                if (spec.Mapper is null)
                {
                    // Plain / custom-repository endpoint: synthesize the default AutoMapper entity↔DTO map.
                    services.Configure<ShiftEntityOptions>(o => o.AddEndpointDefaultMap(spec.Entity, spec.ListDto, spec.ViewDto));
                }
                else
                {
                    // A [ShiftEntityEndpoint<…, TMapper>] entity keeps the built-in repository but supplies a
                    // custom mapper. Register it as IShiftEntityMapper<Entity, ListDto, ViewDto> so the built-in
                    // repository resolves and prefers it over AutoMapper (see ShiftRepository.InitCommon). No
                    // AutoMapper default map is synthesized — the entity has opted out of AutoMapper for these
                    // DTOs, so an entity↔DTO map AutoMapper may not even be able to express is not built.
                    var mapperInterface = typeof(IShiftEntityMapper<,,>).MakeGenericType(spec.Entity, spec.ListDto, spec.ViewDto);
                    services.TryAddScoped(mapperInterface, spec.Mapper);
                }
            }
        }

        return services;
    }

    /// <summary>
    /// Registers the TypeAuth <paramref name="action"/> for <typeparamref name="TEntity"/> in the
    /// <see cref="ShiftEntityActionMap"/>. Cross-entity surfaces (for example the standalone
    /// attention endpoints) read that map to apply the same permission check as the entity's own
    /// endpoints.
    /// </summary>
    /// <remarks>
    /// Attribute endpoints and <c>MapShiftEntitySecureCrud</c> feed the map automatically, so
    /// this call is only needed for entities served by a classic
    /// <c>ShiftEntitySecureControllerAsync</c>. The controller receives its action through its
    /// constructor, so the framework cannot see the action at startup. Safe to call before or
    /// after <c>RegisterShiftRepositories</c> — both write into the same singleton instance.
    /// Registering the same entity again overwrites the previous action.
    /// </remarks>
    public static IServiceCollection AddShiftEntityAction<TEntity>(this IServiceCollection services, ReadWriteDeleteAction action)
        where TEntity : ShiftEntityBase
    {
        GetOrAddShiftEntityActionMap(services).Register(typeof(TEntity).Name, action);

        return services;
    }

    // Same pattern as the ShiftEntityDtoMap registration above: the map is registered as a
    // singleton INSTANCE, so every caller — RegisterShiftRepositories and AddShiftEntityAction,
    // in any order — finds the existing instance and writes into it.
    private static ShiftEntityActionMap GetOrAddShiftEntityActionMap(IServiceCollection services)
    {
        var existing = services.FirstOrDefault(d => d.ServiceType == typeof(ShiftEntityActionMap));

        if (existing?.ImplementationInstance is ShiftEntityActionMap actionMap)
            return actionMap;

        var created = new ShiftEntityActionMap();
        services.AddSingleton(created);
        return created;
    }
}
