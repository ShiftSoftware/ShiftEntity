using System;
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
    /// The value of the caller's first <paramref name="claimType"/> claim, or <see langword="null"/> when there is no
    /// signed-in user or no such claim. The value is returned verbatim (in whatever encoding the claim stores — e.g. a
    /// hashed id), to be decoded by the dimension's own converter so the self/owner id lands in the same id-space as
    /// the dimension's grant ids. An absent claim resolves to <see langword="null"/>, which the policy treats as
    /// "no self/owner id" — fail closed, never wildcard.
    /// </summary>
    public string? GetClaim(string claimType) => user?.FindFirst(claimType)?.Value;
}
