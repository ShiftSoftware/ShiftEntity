namespace ShiftSoftware.ShiftEntity.Core.DataLevelAccess;

/// <summary>
/// What a caller sees when they View (fetch by id / idempotency key) a row that exists but is outside their declared
/// data-level scope — the 404-vs-403 disclosure choice (D7), made deliberate per entity via
/// <see cref="DataLevelAccessBuilder{TEntity}.WhenDenied"/>. Applies to the single-row View only: lists always
/// filter (an out-of-scope row simply isn't in them — there is nothing to deny), and Insert/Edit/Delete denials are
/// always 403 (the caller already holds the row; there is nothing left to disclose).
/// </summary>
public enum DataLevelDeniedBehavior
{
    /// <summary>
    /// The default: the single-row fetch stays data-level filtered, so an out-of-scope row is invisible — the caller
    /// gets <see langword="null"/> (surfaced as 404), indistinguishable from a row that doesn't exist. Discloses
    /// nothing about the row's existence.
    /// </summary>
    NotFound = 0,

    /// <summary>
    /// The single-row fetch deliberately skips the data-level query filter and lets the row check deny: an
    /// out-of-scope row that exists is refused loudly with 403 "Can Not Read Item", while a row that doesn't exist
    /// still comes back <see langword="null"/> (404). Discloses that the row exists — choose it only where that is
    /// acceptable.
    /// </summary>
    Forbidden = 1,
}
