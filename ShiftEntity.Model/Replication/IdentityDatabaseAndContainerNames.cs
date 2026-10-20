namespace ShiftSoftware.ShiftEntity.Model.Replication;

public class IdentityDatabaseAndContainerNames
{
    public const string DatabaseName = "Identity";
    public const string CompanyContainerName = "Companies";
    /// <summary>
    /// The container replication wrote branches and their link documents to before <see cref="BranchContainerName"/>.
    /// The framework no longer writes it; it stays so that hosts that have not upgraded still compile.
    /// </summary>
    public const string CompanyBranchContainerName = "CompanyBranches";

    /// <summary>
    /// One <see cref="IdentityModels.BranchModel"/> per branch, with its services, departments and brands inside it.
    /// Partitioned by <c>/id</c>.
    /// </summary>
    public const string BranchContainerName = "Branches";
    public const string CountryContainerName = "Countries";
    public const string ServiceContainerName = "Services";
    public const string DepartmentContainerName = "Departments";
    public const string BrandContainerName = "Brands";
    public const string TeamContainerName = "Teams";
    public const string UserContainerName = "Users";
    public const string AccessTreeContainerName = "AccessTrees";
    public const string UserAccessTreeContainerName = "UserAccessTrees";
    public const string TeamUserContainerName = "TeamUsers";
}
