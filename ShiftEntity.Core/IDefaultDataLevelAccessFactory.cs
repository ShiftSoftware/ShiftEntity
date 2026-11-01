using ShiftSoftware.TypeAuth.Core;

namespace ShiftSoftware.ShiftEntity.Core;

/// <summary>
/// Builds the legacy data-level evaluator (<see cref="IDefaultDataLevelAccess"/>) for any user, the same way a request
/// builds it for its own signed-in user. A repository uses it to check its legacy data-level rules for another user
/// (<see cref="IShiftRepositoryAsync{Entity, ListDTO, ViewAndUpsertDTO}.CanAccessAs"/>).
/// <para>
/// The web host registers it next to <see cref="IDefaultDataLevelAccess"/>. ShiftEntity.EFCore does not reference the
/// web package, so a repository finds the evaluator for another user only through this factory.
/// </para>
/// </summary>
public interface IDefaultDataLevelAccessFactory
{
    /// <summary>
    /// The legacy evaluator for the user that <paramref name="user"/> describes, with that user's grants
    /// (<paramref name="typeAuth"/>) and claims.
    /// </summary>
    IDefaultDataLevelAccess Create(ITypeAuthService typeAuth, ICurrentUserProvider user);
}
