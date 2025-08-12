namespace ShiftSoftware.ShiftEntity.Model.Replication.IdentityModels;

public class TeamModel : ReplicationModel
{
    public string Name { get; set; }
    public string? IntegrationId { get; set; }
    public List<string> Tags { get; set; } = new();
    public List<CompanyBranchSubItemModel> CompanyBranches { get; set; } = new();
}