using Microsoft.AspNetCore.Mvc;

namespace ShiftSoftware.ShiftEntity.Web.Extensions;

internal static class ControllerBaseExtensions
{
    internal static long? GetUserID(this ControllerBase controller)
    {
        return controller.HttpContext.GetUserID();
    }
}
