using Microsoft.AspNetCore.Http;
using System.Security.Claims;

namespace ShiftSoftware.ShiftEntity.Web.Extensions;

public static class HttpContextExtensions
{
    internal static long? GetUserID(this HttpContext httpContext)
    {
        if (httpContext is null || httpContext.User.Identity is null || !httpContext.User.Identity.IsAuthenticated)
            return null;

        var id = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (id is null)
            return null;

        return Services.ShiftEntityHashIds.Decode<ShiftIdentity.Core.DTOs.User.UserDTO>(id);
    }
}
