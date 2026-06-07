namespace ShiftSoftware.ShiftEntity.Model.Flags;

/// <summary>
/// Org/location claim marker: <c>RegionID</c> drives the standard region data-level access dimension and is
/// backfilled on insert from the acting user's claim by the audit-stamping sweep.
/// </summary>
public interface IEntityHasRegion<Entity>
{
    long? RegionID { get; set; }
}
