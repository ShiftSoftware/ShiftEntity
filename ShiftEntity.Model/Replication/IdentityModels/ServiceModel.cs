using ShiftSoftware.ShiftEntity.Model.Replication;

namespace ShiftSoftware.ShiftEntity.Model.Replication.IdentityModels;

public class ServiceModel : ReplicationModel
{
    public string Name { get; set; } = default!;
    public string? IntegrationId { get; set; }
}
