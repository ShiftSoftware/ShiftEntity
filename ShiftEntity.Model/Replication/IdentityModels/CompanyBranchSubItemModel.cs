using ShiftSoftware.ShiftEntity.Model.Replication;

namespace ShiftSoftware.ShiftEntity.Model.Replication.IdentityModels;

/// <summary>
/// A link document of the <c>CompanyBranches</c> container, which replication no longer writes. It stays because
/// <see cref="TeamModel.CompanyBranches"/> lists a team's branches with it.
/// </summary>
public class CompanyBranchSubItemModel : ReplicationModel
{
    public string Name { get; set; } = default!;
    public string? IntegrationId { get; set; } = default!;

    //Partition keys
    public string BranchID { get; set; } = default!;
    public string ItemType { get; set; } = default!;
}