using AutoMapper;
using AutoMapper.Internal;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.DataLevelAccess;
using ShiftSoftware.ShiftEntity.Core.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Microsoft.Extensions.DependencyInjection;

public static class IServiceCollectionExtensions
{
    /// <summary>
    /// Registers only <see cref="IHashIdService"/> and the <see cref="HashIdOptions"/> slice of
    /// <see cref="ShiftEntityOptions"/>. Use this from hosts that need HashId encode/decode but
    /// don't want the rest of the ShiftEntity stack (AutoMapper scanning, Azure storage converters,
    /// claim providers, MVC plumbing) — e.g. data sync agents, console tools, or any non-web
    /// process. For MVC hosts use <c>AddShiftEntityWeb</c>; for Azure Functions Worker hosts that
    /// use the AspNetCore extension use <c>AddShiftEntityFunctions</c>.
    /// </summary>
    public static IServiceCollection AddShiftEntityHashId(this IServiceCollection services, Action<HashIdOptions> configure)
    {
        services.Configure<ShiftEntityOptions>(o => configure(o.HashId));
        return services.AddShiftEntityHashIdCore();
    }

    /// <summary>
    /// Registers <see cref="IHashIdService"/> as a singleton without touching options. Shared by
    /// the narrow <see cref="AddShiftEntityHashId"/> entry point and the broader
    /// <c>AddShiftEntity</c> internal registration so the service descriptor lives in exactly one
    /// place.
    /// </summary>
    private static IServiceCollection AddShiftEntityHashIdCore(this IServiceCollection services)
    {
        services.TryAddSingleton<IHashIdService, HashIdService>();
        return services;
    }

    /// <summary>
    /// Registers the v2 data-level-access engine's per-request services: <see cref="IAccessibleItemsSource"/>
    /// (TypeAuth-backed, memoized per request) and the <see cref="DataLevelAccessContext"/> consumed by
    /// <c>DataLevelAccessPolicy</c>'s query filter and row check. <c>AddShiftEntityWeb</c> and
    /// <c>AddShiftEntityFunctions</c> call this internally; call it directly only from custom hosts
    /// that don't go through either entry point.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Both registrations are scoped — by design, never singleton.</b> The source wraps the request's scoped
    /// <c>ITypeAuthService</c> (the caller's grants) and caches per request; the context resolves the caller's
    /// <see cref="System.Security.Claims.ClaimsPrincipal"/> once. A longer lifetime would serve one user's
    /// accessible ids to another (see <see cref="TypeAuthAccessibleItemsSource"/>).
    /// </para>
    /// <para>
    /// Resolving <see cref="DataLevelAccessContext"/> expects <c>ITypeAuthService</c> (TypeAuth's
    /// <c>AddTypeAuth</c>), <see cref="ICurrentUserProvider"/> (the web/functions entry points), and
    /// <see cref="IHashIdService"/> (<c>AddShiftEntity</c> / <see cref="AddShiftEntityHashId"/>) to be registered.
    /// Registrations use <c>TryAdd</c>, so a custom <see cref="IAccessibleItemsSource"/> registered beforehand wins.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddShiftEntityDataLevelAccess(this IServiceCollection services)
    {
        services.TryAddScoped<IAccessibleItemsSource, TypeAuthAccessibleItemsSource>();
        services.TryAddScoped<DataLevelAccessContext>();

        return services;
    }

    /// <summary>
    /// Registers ShiftEntity core services with the given configuration.
    /// Multiple calls to <c>services.Configure&lt;ShiftEntityOptions&gt;(...)</c> are additive.
    /// Internal — consumers go through <c>AddShiftEntityWeb</c> (MVC hosts) or
    /// <c>AddShiftEntityFunctions</c> (Functions Worker AspNetCore hosts), which both call
    /// this internally so the core is wired exactly once.
    /// </summary>
    internal static IServiceCollection AddShiftEntity(this IServiceCollection services, Action<ShiftEntityOptions> configure)
    {
        services.Configure(configure);

        return services.AddShiftEntity();
    }

    /// <summary>
    /// Registers ShiftEntity core infrastructure without configuring options.
    /// Options can be registered separately via <c>services.Configure&lt;ShiftEntityOptions&gt;(o => { ... })</c>.
    /// Internal — see the overload above for the rationale.
    /// </summary>
    internal static IServiceCollection AddShiftEntity(this IServiceCollection services)
    {
        // Expose ShiftEntityOptions as a singleton resolved from IOptions (backward compat)
        services.TryAddSingleton(sp => sp.GetRequiredService<IOptions<ShiftEntityOptions>>().Value);

        // HashId service — reads its configuration from ShiftEntityOptions.HashId, populated via
        // the fluent x.HashId.RegisterHashId(...) / RegisterIdentityHashId(...) API inside
        // AddShiftEntityWeb.
        services.AddShiftEntityHashIdCore();

        // Register AzureStorageService lazily from merged options
        services.TryAddSingleton(sp =>
        {
            var options = sp.GetRequiredService<ShiftEntityOptions>();
            return new AzureStorageService(options.azureStorageOptions);
        });

        // Register AutoMapper with deferred assembly scanning —
        // all Configure<ShiftEntityOptions> calls have completed by the time
        // the MapperConfiguration singleton is first resolved.
        // DefaultAutoMapperProfile's assembly is passed statically so AutoMapper
        // scans it and registers its internal type converters in DI.
        // Profiles found in AutoMapperAssemblies are constructed through ActivatorUtilities so
        // they can take DI dependencies (e.g. IHashIdService) via constructor.
        services.AddAutoMapper((sp, cfg) =>
        {
            var options = sp.GetRequiredService<ShiftEntityOptions>();

            // 1. Maps auto-built from repository generic arguments.
            cfg.AddProfile(new DefaultAutoMapperProfile(options.DataAssemblies.ToArray()));

            // 2. User-written profiles.
            foreach (var assembly in options.AutoMapperAssemblies)
            {
                foreach (var profileType in assembly.GetTypes()
                    .Where(t => typeof(Profile).IsAssignableFrom(t) && !t.IsAbstract && t != typeof(Profile)))
                {
                    var profile = (Profile)ActivatorUtilities.CreateInstance(sp, profileType);
                    cfg.AddProfile(profile);
                }
            }

            // 3. Default maps for attribute-driven endpoints whose entities have no repository class.
            //    Added LAST and deduped against everything above so a repository scan or a custom user
            //    profile for the same pair is kept — AutoMapper would otherwise let this later profile
            //    silently override it.
            //    Under GeneratedFirst/GeneratedOnly a triple the registry covers gets its mapper from the
            //    registry (see ShiftRepository.InitCommon), so synthesizing an AutoMapper map for it is dead
            //    weight — and this is the last framework path that reaches for AutoMapper BY DEFAULT, which
            //    has to stop before Stage F can delete the profile machinery.
            //
            //    The filter lives HERE rather than in RegisterShiftRepositories because the mode is only
            //    final once every Configure<ShiftEntityOptions> callback has run, and this factory is
            //    deferred until IMapper is first resolved — so by now it is.
            var endpointMaps = options.MappingMode == ShiftEntityMappingMode.AutoMapperFirst
                ? options.EndpointDefaultMaps
                : options.EndpointDefaultMaps.Where(m => !HasGeneratedMapper(m)).ToList();

            if (endpointMaps.Count > 0)
                cfg.AddProfile(new DefaultAutoMapperProfile(endpointMaps, GetConfiguredPairs(cfg)));
        }, typeof(DefaultAutoMapperProfile).Assembly);

        return services;
    }

    /// <summary>
    /// True when the source generator produced a mapper for this endpoint's <c>[entity, listDto, viewDto]</c>.
    /// The module initializer that populates the registry is not triggered by reflection alone, so force it
    /// first — otherwise the answer depends on whatever happened to touch the assembly earlier.
    /// </summary>
    private static bool HasGeneratedMapper(Type[] triple)
    {
        try
        {
            System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(triple[0].Module.ModuleHandle);
            return ShiftEntityMapperRegistry.Find(triple[0], triple[1], triple[2]) is not null;
        }
        catch
        {
            // A consumer assembly whose module initializer throws must not take down mapper configuration —
            // keeping the AutoMapper map is the safe answer, since it is what would have been built anyway.
            return false;
        }
    }

    // The (source, dest) pairs already declared by the profiles added to cfg so far, via AutoMapper 14's
    // Internal() escape hatch. Used to dedupe the endpoint-map profile so it never re-creates (and thus
    // overrides) a map a repository scan or user profile already defined. ReverseMap targets aren't
    // materialized at this point — fine, we key on the forward pair and skip the whole forward+reverse.
    private static HashSet<(Type Source, Type Destination)> GetConfiguredPairs(IMapperConfigurationExpression cfg)
    {
        var pairs = new HashSet<(Type, Type)>();

        foreach (var profile in cfg.Internal().Profiles)
        {
            foreach (var tm in profile.TypeMapConfigs)
                pairs.Add((tm.Types.SourceType, tm.Types.DestinationType));

            foreach (var tm in profile.OpenTypeMapConfigs)
                pairs.Add((tm.Types.SourceType, tm.Types.DestinationType));
        }

        return pairs;
    }
}
