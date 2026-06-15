using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ShiftSoftware.ShiftEntity.Core.Tagging;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.EFCore.Tagging;
using ShiftSoftware.ShiftEntity.Model.Dtos.Tagging;
using ShiftSoftware.ShiftEntity.Web.Endpoints;

namespace ShiftSoftware.ShiftEntity.Web.Tagging;

public static class TaggingEndpoints
{
    /// <summary>
    /// Maps the tag-vocabulary REST endpoints for the calling microservice.
    /// Requires <c>services.AddShiftTagging&lt;TDbContext&gt;()</c> to have been called first.
    /// Returns the route group so callers can chain additional configuration.
    /// </summary>
    /// <remarks>
    /// Endpoints registered (GET list/single, POST, PUT, DELETE):
    /// <list type="bullet">
    ///   <item>If a tagging <c>Action</c> was supplied to <c>AddShiftTagging</c>, the endpoints
    ///   require authentication and are gated by that action's Read/Write/Delete access levels.</item>
    ///   <item>If NO action was supplied (e.g. <c>services.AddShiftTagging&lt;DB&gt;()</c>), the
    ///   endpoints are mapped <b>anonymously</b> — no authentication required.</item>
    /// </list>
    /// Autocomplete is served by the standard OData list endpoint via
    /// <c>?$filter=contains(Name,'…')&amp;$top=N</c> — no separate autocomplete route is needed.
    /// <see cref="ShiftSoftware.ShiftBlazor.Components.ShiftAutocomplete{TEntitySet}"/> consumes
    /// this endpoint directly.
    ///
    /// Does nothing when <see cref="ShiftTaggingOptions.SkipEndpointRegistration"/> is set,
    /// or when <c>AddShiftTagging</c> was not called.
    /// </remarks>
    public static IEndpointRouteBuilder MapShiftTaggingEndpoints<TDbContext>(this IEndpointRouteBuilder endpoints)
        where TDbContext : ShiftDbContext
    {
        var options = endpoints.ServiceProvider.GetService<IOptions<ShiftTaggingOptions>>()?.Value;
        if (options is null || !options.Enabled || options.SkipEndpointRegistration)
            return endpoints;

        if (options.Action is not null)
        {
            // Secured: authentication required + TypeAuth gate on the supplied action node.
            endpoints.MapShiftEntitySecureCrud<ShiftTagRepository<TDbContext>, Tag, TagListDTO, TagDTO>(
                options.EndpointPrefix,
                options.Action);
        }
        else
        {
            // No action supplied → anonymous endpoints (no authentication).
            endpoints.MapShiftEntityCrud<ShiftTagRepository<TDbContext>, Tag, TagListDTO, TagDTO>(
                options.EndpointPrefix);
        }

        return endpoints;
    }
}
