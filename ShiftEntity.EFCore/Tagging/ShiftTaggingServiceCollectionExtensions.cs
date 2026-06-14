using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Tagging;
using ShiftSoftware.TypeAuth.Core.Actions;
using System;

namespace ShiftSoftware.ShiftEntity.EFCore.Tagging;

public static class ShiftTaggingServiceCollectionExtensions
{
    /// <summary>
    /// Opt the microservice into the framework tagging system.
    /// <list type="bullet">
    ///   <item>Includes the <see cref="Tag"/> entity in the model (it is otherwise ignored).</item>
    ///   <item>Auto-wires many-to-many join tables for every <see cref="IShiftEntityTaggable"/> entity.</item>
    ///   <item>Registers <see cref="ShiftTagRepository{TDbContext}"/> as a scoped service.</item>
    ///   <item>Adds <c>Tag → TagDTO</c> AutoMapper maps via the standard
    ///         <c>ShiftEntityOptions.AddAutoMapper</c> path.</item>
    /// </list>
    /// Call <c>app.MapShiftTaggingEndpoints&lt;TDbContext&gt;()</c> in the Web layer to expose
    /// the REST surface, or set <see cref="ShiftTaggingOptions.SkipEndpointRegistration"/> = true
    /// and write your own.
    /// </summary>
    public static IServiceCollection AddShiftTagging<TDbContext>(
        this IServiceCollection services,
        Action<ShiftTaggingOptions>? configure = null)
        where TDbContext : ShiftDbContext
    {
        services.Configure<ShiftTaggingOptions>(opts =>
        {
            opts.Enabled = true;
            configure?.Invoke(opts);
        });

        services.AddScoped<ShiftTagRepository<TDbContext>>();

        services.Configure<ShiftEntityOptions>(opts =>
            opts.AddAutoMapper(typeof(ShiftTaggingAutoMapperProfile).Assembly));

        return services;
    }

    /// <summary>
    /// Convenience overload: pass the action node directly without building an options object.
    /// </summary>
    public static IServiceCollection AddShiftTagging<TDbContext>(
        this IServiceCollection services,
        ReadWriteDeleteAction action)
        where TDbContext : ShiftDbContext
        => services.AddShiftTagging<TDbContext>(opts => opts.Action = action);
}
