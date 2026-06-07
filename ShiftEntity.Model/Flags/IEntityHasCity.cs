namespace ShiftSoftware.ShiftEntity.Model.Flags;

/// <summary>
/// Org/location claim marker: <c>CityID</c> drives the standard city data-level access dimension and is
/// backfilled on insert from the acting user's claim by the audit-stamping sweep.
/// </summary>
public interface IEntityHasCity<Entity>
{
    long? CityID { get; set; }
}
