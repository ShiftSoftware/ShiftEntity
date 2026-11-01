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

    /// <summary>
    /// The IDs of the team's members, in ascending order. The list comes from the team's memberships (the team user
    /// rows), so an empty list means the team has no member.
    /// <para>
    /// Null means the document was written before this list existed, so the members are not known. A reader must not
    /// treat null as an empty list.
    /// </para>
    /// </summary>
    public List<long>? UserIDs { get; set; }
}