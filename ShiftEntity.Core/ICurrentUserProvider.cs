using System.Security.Claims;

namespace ShiftSoftware.ShiftEntity.Core;

public interface ICurrentUserProvider
{
    ClaimsPrincipal? GetUser();
}