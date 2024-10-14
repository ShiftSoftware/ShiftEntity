using ShiftSoftware.ShiftEntity.Model.Replication;

namespace ShiftSoftware.ShiftEntity.Model.Replication.IdentityModels;

public class TeamModel : ReplicationModel
{
    public string Name { get; set; }

    public string? IntegrationId { get; set; }
}
