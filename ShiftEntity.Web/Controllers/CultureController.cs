using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.TypeAuth.AspNetCore;
using ShiftSoftware.TypeAuth.Core;
using System;

[Route("api/[controller]/[action]")]
[AllowAnonymous]
public class CultureController : ControllerBase
{
    public IActionResult Set(string culture, string redirectUri)
    {
        if (culture != null)
        {
            HttpContext.Response.Cookies.Append(
                CookieRequestCultureProvider.DefaultCookieName,
                CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture, culture)),
                new CookieOptions()
                {
                    Expires = DateTime.Now.AddYears(5),
                    HttpOnly = false,
                    Secure = false,
                    SameSite = SameSiteMode.Strict,
                });
        }

        return LocalRedirect(redirectUri);
    }
}