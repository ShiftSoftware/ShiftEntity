using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.TypeAuth.Core;

namespace ShiftSoftware.ShiftEntity.Web.Services;

/// <summary>
/// Builds <see cref="DefaultDataLevelAccess"/> for a given user, with that user's grants and claims instead of the
/// request's. It is what a repository needs to check its legacy data-level rules for another user, for example a
/// notification's recipient.
/// </summary>
public class DefaultDataLevelAccessFactory : IDefaultDataLevelAccessFactory
{
    private readonly IHashIdService hashIdService;

    public DefaultDataLevelAccessFactory(IHashIdService hashIdService)
    {
        this.hashIdService = hashIdService;
    }

    public IDefaultDataLevelAccess Create(ITypeAuthService typeAuth, ICurrentUserProvider user)
        => new DefaultDataLevelAccess(typeAuth, new IdentityClaimProvider(user, hashIdService), hashIdService);
}
