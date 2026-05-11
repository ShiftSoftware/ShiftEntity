using AutoMapper;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Services;
using System;
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
            cfg.AddProfile(new DefaultAutoMapperProfile(options.DataAssemblies.ToArray()));

            foreach (var assembly in options.AutoMapperAssemblies)
            {
                foreach (var profileType in assembly.GetTypes()
                    .Where(t => typeof(Profile).IsAssignableFrom(t) && !t.IsAbstract && t != typeof(Profile)))
                {
                    var profile = (Profile)ActivatorUtilities.CreateInstance(sp, profileType);
                    cfg.AddProfile(profile);
                }
            }
        }, typeof(DefaultAutoMapperProfile).Assembly);

        return services;
    }
}
