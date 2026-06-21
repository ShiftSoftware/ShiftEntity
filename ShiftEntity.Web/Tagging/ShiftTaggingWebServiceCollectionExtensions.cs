using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.EFCore.Tagging;
using ShiftSoftware.TypeAuth.AspNetCore;
using ShiftSoftware.TypeAuth.Core.Actions;

namespace ShiftSoftware.ShiftEntity.Web.Tagging;

public static class ShiftTaggingWebServiceCollectionExtensions
{
    /// <summary>
    /// Opt into tagging (secured by <paramref name="action"/>) <b>and</b> register
    /// <typeparamref name="TActionTree"/> with TypeAuth in one call — so the action tree that owns
    /// the tag node is known to the authorization layer without a separate
    /// <c>AddTypeAuth(o =&gt; o.AddActionTree&lt;TActionTree&gt;())</c>.
    /// <para>
    /// The tree only needs the generic parameter because a <see cref="ReadWriteDeleteAction"/> node
    /// instance doesn't carry its declaring tree type. Registering the same tree again elsewhere is
    /// harmless — <see cref="TypeAuthAspNetCoreOptions.AddActionTree(System.Type)"/> is idempotent —
    /// so apps with other entities keep their explicit <c>AddActionTree</c> and just gain the wiring.
    /// </para>
    /// Forwards to the EFCore <c>AddShiftTagging&lt;TDbContext&gt;(action)</c>; call
    /// <c>app.MapShiftTaggingEndpoints&lt;TDbContext&gt;()</c> as usual to expose the endpoints.
    /// </summary>
    public static IServiceCollection AddShiftTagging<TDbContext, TActionTree>(
        this IServiceCollection services,
        ReadWriteDeleteAction action)
        where TDbContext : ShiftDbContext
        where TActionTree : class
    {
        services.AddShiftTagging<TDbContext>(action);
        services.Configure<TypeAuthAspNetCoreOptions>(o => o.AddActionTree<TActionTree>());

        return services;
    }
}
