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
    /// Endpoints registered, all behind <c>RequireAuthorization</c>:
    /// <list type="bullet">
    ///   <item>GET    {prefix}            — list (OData $filter/$top/$orderby) — <c>Tags.Read</c></item>
    ///   <item>GET    {prefix}/{key}      — single — <c>Tags.Read</c></item>
    ///   <item>POST   {prefix}            — create — <c>Tags.Write</c></item>
    ///   <item>PUT    {prefix}/{key}      — update — <c>Tags.Write</c></item>
    ///   <item>DELETE {prefix}/{key}      — soft delete — <c>Tags.Delete</c></item>
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

        endpoints.MapShiftEntitySecureCrud<ShiftTagRepository<TDbContext>, Tag, TagListDTO, TagDTO>(
            options.EndpointPrefix,
            options.Action);

        return endpoints;
    }
}
