
namespace Microsoft.AspNetCore.Mvc;

internal static class ControllerBaseExtensions
{
    internal static long? GetUserID(this ControllerBase controller)
    {
        return controller.HttpContext.GetUserID();
    }
}
