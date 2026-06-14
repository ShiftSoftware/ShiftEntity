namespace ShiftSoftware.ShiftEntity.Model.Flags;

/// <summary>
/// Org/location claim marker: <c>CompanyID</c> drives the standard company data-level access dimension and is
/// backfilled on insert from the acting user's claim by the audit-stamping sweep.
/// </summary>
public interface IEntityHasCompany<Entity>
{
    long? CompanyID { get; set; }
}
