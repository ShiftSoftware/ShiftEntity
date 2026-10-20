namespace ShiftSoftware.ShiftEntity.Model.Replication.IdentityModels;

/// <summary>
/// The kinds of document the <c>CompanyBranches</c> container held, by their <c>ItemType</c>. Replication no longer
/// writes that container. This stays because readers filter on <c>ItemType == Branch</c>, which the
/// <see cref="BranchModel"/> documents still carry.
/// </summary>
public class CompanyBranchContainerItemTypes
{
    public const string Branch = "Branch";
    public const string Service = "Service";
    public const string Department = "Department";
    public const string Brand = "Brand";
}
