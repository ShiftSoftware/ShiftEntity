namespace ShiftSoftware.ShiftEntity.Core;

/// <summary>
/// Marks an entity that carries "protected" rows (e.g. system-seeded / built-in data) which must never be modified
/// or deleted through the normal CRUD path. The built-in
/// <c>ShiftRepository&lt;DB, EntityType, ListDTO, ViewAndUpsertDTO&gt;</c> enforces this in <c>UpsertAsync</c> /
/// <c>DeleteAsync</c>: an update or delete of a row whose <see cref="IsProtected"/> is <see langword="true"/> is
/// rejected with <c>403 Forbidden</c> (reads and listing are allowed).
/// <para>
/// The flag is per-row (some rows of a type are protected, others are not): the type is <em>protectable</em>, a given
/// row <em>is protected</em>. Opting in is just adding this interface to the class declaration and exposing the flag
/// (any stored property, or an explicit-interface implementation over an existing one). No repository override is
/// needed, so an otherwise simple entity can use an attribute-driven CRUD endpoint on the built-in repository while
/// still protecting its seeded rows.
/// </para>
/// </summary>
public interface IShiftEntityProtectable
{
    bool IsProtected { get; }
}
