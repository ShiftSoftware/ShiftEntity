namespace ShiftSoftware.ShiftEntity.Model.Flags;

/// <summary>
/// Org/location claim marker: <c>CompanyBranchID</c> drives the standard branch data-level access dimension and is
/// backfilled on insert from the acting user's claim by the audit-stamping sweep.
/// </summary>
public interface IEntityHasCompanyBranch<Entity>
{
    long? CompanyBranchID { get; set; }
}
