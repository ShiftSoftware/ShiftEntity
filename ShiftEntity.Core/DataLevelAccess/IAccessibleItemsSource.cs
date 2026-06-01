using ShiftSoftware.TypeAuth.Core;
using ShiftSoftware.TypeAuth.Core.Actions;

namespace ShiftSoftware.ShiftEntity.Core.DataLevelAccess;

/// <summary>
/// The v2 data-level engine's single source of accessible-item sets for a TypeAuth dynamic action. The engine
/// talks to this rather than to <see cref="ITypeAuthService"/> directly so it can be unit-tested with a hand-built
/// source and so the per-request lookup is memoized in one place.
/// </summary>
/// <remarks>
/// Returns the whole <see cref="AccessibleItemsByAccess"/> bundle (Read/Write/Delete/Maximum) in a single call, so
/// a dimension resolves the caller's reachable ids once and then picks the level per operation with
/// <c>For(access)</c> (View ⇒ Read, Insert/Edit ⇒ Write, Delete ⇒ Delete). Implementations are expected to memoize
/// per <paramref name="action"/> (and self-ids) for the lifetime of one request.
/// </remarks>
public interface IAccessibleItemsSource
{
    /// <summary>
    /// Returns the accessible items, per access level, that the caller can reach for <paramref name="action"/>.
    /// </summary>
    /// <param name="action">The TypeAuth dynamic action whose accessible ids are requested.</param>
    /// <param name="selfIds">
    /// IDs treated as "self" for self-reference resolution (e.g. the caller's own company id). Supplied by the
    /// caller so this source stays agnostic of where self-ids come from.
    /// </param>
    /// <returns>The accessible items for each access level (Read/Write/Delete/Maximum).</returns>
    AccessibleItemsByAccess GetByAccess(DynamicAction action, params string[]? selfIds);
}
