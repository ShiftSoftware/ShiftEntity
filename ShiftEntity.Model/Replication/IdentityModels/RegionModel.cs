using ShiftSoftware.ShiftEntity.Model.Replication;

namespace ShiftSoftware.ShiftEntity.Model.Replication.IdentityModels;

public class RegionModel : ReplicationModel
{
    public string RegionID { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string? IntegrationId { get; set; } = default!;
    public string? ShortCode { get; set; }
    public bool BuiltIn { get; set; }
    public string ItemType { get; set; } = default!;
}
