using System;
using System.Collections.Generic;
using ShiftSoftware.TypeAuth.Core;
using ShiftSoftware.TypeAuth.Core.Actions;

namespace ShiftSoftware.ShiftEntity.Core.DataLevelAccess;

/// <summary>
/// <see cref="IAccessibleItemsSource"/> backed by a per-request <see cref="ITypeAuthService"/>. Memoizes
/// <c>GetAccessibleItemsByAccess</c> by action (and self-ids) so the access tree is traversed once per action per
/// request — not once per filtered query or authorized row.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifetime — register scoped (one instance per request).</b> The cache is request-private <i>instance</i> state
/// (there is no static state) and holds results computed from this instance's <see cref="ITypeAuthService"/> — which
/// is itself registered scoped and built from the current user's claims. A scoped lifetime makes the cache live
/// exactly as long as that one user's grants. <b>Never register it as a singleton</b>, and never let a singleton
/// capture it (a captive dependency), or one user's accessible ids could be served to another. ASP.NET Core's scope
/// validation (on by default in Development) flags such captures at startup.
/// </para>
/// <para>
/// Thread-safe: an internal lock guards the cache and serializes the underlying lookup, so concurrent use within a
/// single request (parallel queries, a Blazor circuit) cannot corrupt it.
/// </para>
/// </remarks>
public sealed class TypeAuthAccessibleItemsSource : IAccessibleItemsSource
{
    private readonly ITypeAuthService typeAuth;
    private readonly object gate = new();
    private readonly Dictionary<(DynamicAction Action, string SelfIds), AccessibleItemsByAccess> cache = new();

    public TypeAuthAccessibleItemsSource(ITypeAuthService typeAuth)
    {
        this.typeAuth = typeAuth ?? throw new ArgumentNullException(nameof(typeAuth));
    }

    /// <inheritdoc />
    public AccessibleItemsByAccess GetByAccess(DynamicAction action, params string[]? selfIds)
    {
        if (action is null)
            throw new ArgumentNullException(nameof(action));

        // Actions are readonly-static singletons, so reference equality keys the action correctly; the self-ids
        // are folded into the key because TypeAuth's self-reference resolution depends on them.
        var key = (action, SelfIdsKey(selfIds));

        // The lock keeps the cache consistent and ensures the underlying tree traversal runs at most once per key,
        // never concurrently — so we assume nothing about TypeAuthContext's own thread-safety. Contention is
        // negligible: this is called a handful of times per request, and a request is usually single-threaded.
        lock (gate)
        {
            if (!cache.TryGetValue(key, out var result))
            {
                result = typeAuth.GetAccessibleItemsByAccess(action, selfIds);
                cache[key] = result;
            }

            return result;
        }
    }

    // Join with '|' (delimiter, not concatenation) so distinct self-id sets can't collide on the cache key
    // (e.g. ["1","23"] vs ["12","3"]). Ids here are numeric or alphanumeric hashids, so '|' never appears in one.
    private static string SelfIdsKey(string[]? selfIds)
        => selfIds is null || selfIds.Length == 0 ? string.Empty : string.Join("|", selfIds);
}
