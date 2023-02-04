using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Web.Extensions;

internal static class ControllerBaseExtensions
{
    internal static Guid? GetUserID(this ControllerBase controller)
    {
        if(!controller.User.Identity.IsAuthenticated)
            return null;

        var id = controller.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (id is null)
            return null;

        return new Guid(id);
    }
}
