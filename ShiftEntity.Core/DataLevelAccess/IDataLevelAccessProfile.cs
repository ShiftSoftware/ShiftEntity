namespace ShiftSoftware.ShiftEntity.Core.DataLevelAccess;

/// <summary>
/// Contributes host-wide default data-level dimensions for an entity. ShiftEntity.Web exposes the standard
/// seven-marker implementation through an explicit host opt-in; custom hosts can seed their own defaults without
/// coupling ShiftEntity.Core or EFCore to ShiftIdentity.
/// </summary>
/// <remarks>
/// A profile contributes defaults only. ShiftRepository centrally overlays the repository's validated explicit
/// declaration afterward, so a profile cannot accidentally drop or weaken it. Return no dimensions when the profile
/// does not apply to <typeparamref name="TEntity"/>. Profiles must not call <c>Unscoped()</c> or
/// <c>WhenDenied(...)</c>; opting an entity out and choosing whether denied rows are disclosed are explicit
/// per-repository decisions.
/// </remarks>
/// <typeparam name="TEntity">The repository entity type.</typeparam>
public interface IDataLevelAccessProfile<TEntity>
{
    /// <summary>Adds zero or more default dimensions to <paramref name="builder"/>.</summary>
    void AddDimensions(DataLevelAccessBuilder<TEntity> builder, DefaultDataLevelAccessOptions options);
}
