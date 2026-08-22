using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Tagging;
using ShiftSoftware.ShiftEntity.Model.Dtos.Tagging;
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
    ///   <item>Auto-includes the <c>Tags</c> navigation on single-entity reads and auto-maps it onto
    ///         <see cref="Model.Dtos.Tagging.IShiftEntityTaggableDTO"/> DTOs — entity mappers never touch Tags.</item>
    ///   <item>Registers <see cref="ShiftTagRepository{TDbContext}"/> as a scoped service.</item>
    ///   <item>Adds <c>Tag → TagDTO</c> AutoMapper maps via the standard
    ///         <c>ShiftEntityOptions.AddAutoMapper</c> path.</item>
    /// </list>
    /// Call <c>app.MapShiftTaggingEndpoints&lt;TDbContext&gt;()</c> in the Web layer to expose
    /// the REST surface, or set <see cref="ShiftTaggingOptions.SkipEndpointRegistration"/> = true
    /// and write your own.
    ///
    /// <para>
    /// Permissions: supply a tagging action via the <see cref="ReadWriteDeleteAction"/> overload (or
    /// <c>opts.Action</c>) to secure the endpoints with TypeAuth. Call this WITHOUT an action
    /// (e.g. <c>services.AddShiftTagging&lt;DB&gt;()</c>) to expose the tag endpoints
    /// <b>anonymously</b> — no authentication — which is the simplest setup for services that
    /// don't need to gate the tag vocabulary.
    /// </para>
    /// <para>
    /// In a web host, prefer the <c>AddShiftTagging&lt;TDbContext, TActionTree&gt;</c> overload in
    /// <c>ShiftSoftware.ShiftEntity.Web.Tagging</c> — it forwards here <b>and</b> registers the action
    /// tree with TypeAuth in one call. These base overloads live in EFCore (not Web) so a non-web
    /// host can enable tagging without taking an ASP.NET Core / TypeAuth.AspNetCore dependency.
    /// </para>
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

        // The framework's own mapper for the Tag triple. Registered explicitly because the generator cannot
        // reach it — it is attached as an analyzer only to ShiftEntity.Core, so a triple whose repository lives
        // in ShiftEntity.EFCore never gets generated code, and ShiftTagRepository is framework-owned, so a
        // consumer has no seam to supply one. TryAdd, so a host that wants its own tag mapping still wins.
        // ShiftRepository resolves it from DI; ShiftTagRepository's two-argument constructor also accepts it.
        services.TryAddScoped<IShiftEntityMapper<Tag, TagListDTO, TagDTO>, ShiftTagMapper>();

        // Still registered: the AutoMapper tagging profile remains the fallback until the fallback itself goes
        // in Stage F. With ShiftTagMapper in DI it is no longer what serves Tag CRUD.
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
