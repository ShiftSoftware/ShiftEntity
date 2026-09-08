using System;
using System.Linq;
using System.Security.Claims;

namespace ShiftSoftware.ShiftEntity.Core.DataLevelAccess;

/// <summary>
/// The per-request services the v2 engine needs to resolve a <see cref="DataLevelAccessPolicy{TEntity}"/> against the
/// caller: the accessible-item <see cref="Source"/> (TypeAuth grants), the caller's principal (for <c>Self</c> /
/// <c>OnOwner</c> claims), and the <see cref="HashIds"/> service (for <c>HashId&lt;TDto&gt;</c> id decoding/encoding).
/// </summary>
/// <remarks>
/// Bundling them into one object keeps both the query path (<see cref="DataLevelAccessPolicy{TEntity}.ApplyQueryFilter"/>)
/// and the row path (Phase 2.4) to a single context parameter, and resolves the caller's <see cref="ClaimsPrincipal"/>
/// exactly once. Construct it per request from the scoped DI services — the same lifetime as
/// <see cref="IAccessibleItemsSource"/> (see <see cref="TypeAuthAccessibleItemsSource"/>) — so one caller's claims and
/// grants are never reused for another.
/// </remarks>
public sealed class DataLevelAccessContext
{
    private readonly ClaimsPrincipal? user;

    /// <summary>Resolves the caller's TypeAuth-accessible id sets (memoized per request).</summary>
    public IAccessibleItemsSource Source { get; }

    /// <summary>Decodes/encodes hashid-keyed dimension ids (<c>HashId&lt;TDto&gt;</c>).</summary>
    public IHashIdService HashIds { get; }

    public DataLevelAccessContext(IAccessibleItemsSource source, ICurrentUserProvider currentUserProvider, IHashIdService hashIds)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        HashIds = hashIds ?? throw new ArgumentNullException(nameof(hashIds));

        if (currentUserProvider is null)
            throw new ArgumentNullException(nameof(currentUserProvider));

        // Resolve the principal once: it is stable for the request, and Self/OnOwner read several claims off it.
        user = currentUserProvider.GetUser();
    }

    /// <summary>
    /// All values of the caller's <paramref name="claimType"/> claims, or an empty array when there is no
    /// <em>authenticated</em> user or no such claim. Requiring <c>Identity.IsAuthenticated</c> matches the legacy
    /// claim reads (<c>ClaimsPrincipalExtensions.GetClaimValues</c>) — claims on an unauthenticated principal must
    /// never grant data access (4.1 parity alignment; fail closed). Values are returned verbatim (in whatever
    /// encoding the claims store — e.g. hashed ids), to be decoded by the dimension's own converter so self ids land
    /// in the same id-space as grant ids. Returning every value matters for multi-membership claims such as Teams.
    /// </summary>
    public string[] GetClaims(string claimType)
        => user?.Identity?.IsAuthenticated == true
            ? user.FindAll(claimType).Select(claim => claim.Value).ToArray()
            : Array.Empty<string>();

    /// <summary>
    /// The first value returned by <see cref="GetClaims"/>, or <see langword="null"/> when none exists. Owner
    /// dimensions and ordinary <c>Self</c> dimensions intentionally remain single-valued; <c>SelfMany</c> consumes
    /// all values.
    /// </summary>
    public string? GetClaim(string claimType) => GetClaims(claimType).FirstOrDefault();
}
