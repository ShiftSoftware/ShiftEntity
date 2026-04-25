using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Services;
using System;
using System.Reflection;

namespace Microsoft.Extensions.DependencyInjection;

public static class IServiceCollectionExtensions
{
    /// <summary>
    /// Registers ShiftEntity core services with the given configuration.
    /// Multiple calls to <c>services.Configure&lt;ShiftEntityOptions&gt;(...)</c> are additive.
    /// </summary>
    public static IServiceCollection AddShiftEntity(this IServiceCollection services, Action<ShiftEntityOptions> configure)
    {
        // Apply the configure lambda eagerly against a throwaway instance so the static side
        // effects inside HashIdOptions.RegisterHashId / RegisterIdentityHashId
        // (HashId.Enabled, HashId.IdentityHashIdSalt, ...) fire at registration time. Otherwise
        // the lambda would only run when IOptions<ShiftEntityOptions>.Value is first resolved,
        // which never happens automatically in non-MVC hosts (Azure Functions Worker, console),
        // leaving the legacy HashId statics unset and JsonHashIdConverterAttribute disabled.
        configure(new ShiftEntityOptions());

        // Also register through the Options pattern so the canonical instance picks up the same
        // configuration when materialized, and so additional Configure<ShiftEntityOptions>(...)
        // calls compose additively.
        services.Configure(configure);

        return services.AddShiftEntity();
    }

    /// <summary>
    /// Registers ShiftEntity core infrastructure without configuring options.
    /// Options can be registered separately via <c>services.Configure&lt;ShiftEntityOptions&gt;(o => { ... })</c>.
    /// </summary>
    public static IServiceCollection AddShiftEntity(this IServiceCollection services)
    {
        // Expose ShiftEntityOptions as a singleton resolved from IOptions (backward compat)
        services.TryAddSingleton(sp => sp.GetRequiredService<IOptions<ShiftEntityOptions>>().Value);

        // Non-static HashId service — reads its configuration from ShiftEntityOptions.HashId,
        // populated via the fluent x.HashId.RegisterHashId(...) / RegisterIdentityHashId(...) API
        // inside AddShiftEntityWeb. Replaces the static HashId / ShiftEntityHashIdService path.
        services.TryAddSingleton<IHashIdService, HashIdService>();

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
        services.AddAutoMapper((sp, cfg) =>
        {
            var options = sp.GetRequiredService<ShiftEntityOptions>();
            cfg.AddProfile(new DefaultAutoMapperProfile(options.DataAssemblies.ToArray()));
            cfg.AddMaps(options.AutoMapperAssemblies);
        }, typeof(DefaultAutoMapperProfile).Assembly);

        return services;
    }
}
