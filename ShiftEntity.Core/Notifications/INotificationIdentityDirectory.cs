using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.TypeAuth.Core;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Core.Notifications;

/// <summary>A user who could sign in now, with what the access checks need.</summary>
public sealed record NotificationUser(
    long UserID,
    ITypeAuthService TypeAuth,
    ICurrentUserProvider Claims,
    string? Email,
    bool EmailVerified);

/// <summary>The outcome for the users asked about. Each one is in exactly one of the three.</summary>
/// <param name="Eligible">The users who could sign in now.</param>
/// <param name="NotEligible">The users who could not, with the reason. This is not an error.</param>
/// <param name="Unresolved">The users whose access can't be worked out, with the reason. They get nothing.</param>
public sealed record NotificationUserLookup(
    IReadOnlyList<NotificationUser> Eligible,
    IReadOnlyDictionary<long, string> NotEligible,
    IReadOnlyDictionary<long, string> Unresolved);

/// <summary>
/// What the send path reads about people and branches. ShiftIdentity implements it from its replicated documents. It is
/// resolved once per event, so one event is one resolver scope. Without it, an event reaches no users, teams or branch
/// tags, and that is logged. Plain addresses and customers don't need it.
/// </summary>
public interface INotificationIdentityDirectory
{
    /// <summary>Whether each of these users could sign in now.</summary>
    Task<NotificationUserLookup> LookUpUsersAsync(IReadOnlyCollection<long> userIDs, CancellationToken cancellationToken = default);

    /// <summary>The members of a team, or null when they aren't known.</summary>
    Task<IReadOnlyList<long>?> GetTeamMembersAsync(long teamID, CancellationToken cancellationToken = default);

    /// <summary>The tagged addresses on a branch, or null when the branch has no document.</summary>
    Task<IReadOnlyList<TaggedTextDTO>?> GetBranchEmailsAsync(long branchID, CancellationToken cancellationToken = default);
}
