namespace ShiftSoftware.ShiftEntity.Model.Flags;

/// <summary>
/// Org/location claim marker: <c>CountryID</c> drives the standard country data-level access dimension and is
/// backfilled on insert from the acting user's claim by the audit-stamping sweep.
/// </summary>
public interface IEntityHasCountry<Entity>
{
    long? CountryID { get; set; }
}
