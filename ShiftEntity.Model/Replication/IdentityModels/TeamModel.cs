using ShiftSoftware.ShiftEntity.Model.Flags;

namespace ShiftSoftware.ShiftEntity.Model.Replication.IdentityModels;

public class TeamModel : ReplicationModel, IEntityHasCompany<TeamModel>, IEntityHasTeam<TeamModel>
{
    public string Name { get; set; }
    public string? IntegrationId { get; set; }
    public List<string> Tags { get; set; } = new();
    public long? CompanyID { get; set; }
    public long? TeamID { get; set; }
    public List<CompanyBranchSubItemModel> CompanyBranches { get; set; } = new();
}