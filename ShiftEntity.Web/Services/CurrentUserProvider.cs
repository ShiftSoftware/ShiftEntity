using Microsoft.AspNetCore.Http;
using ShiftSoftware.ShiftEntity.Core;
using System.Security.Claims;

namespace ShiftSoftware.ShiftEntity.Web.Services;

public class CurrentUserProvider : ICurrentUserProvider
{
    private readonly ClaimsPrincipal? claimsPrincipal;

    public CurrentUserProvider(IHttpContextAccessor httpContextAccessor)
    {
        this.claimsPrincipal = httpContextAccessor.HttpContext?.User;
    }

    public ClaimsPrincipal? GetUser()
    {
        return this.claimsPrincipal;
    }
}