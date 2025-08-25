using ShiftSoftware.ShiftEntity.Model.Flags;
using ShiftSoftware.ShiftEntity.Model.Replication;

namespace ShiftSoftware.ShiftEntity.Model.Replication.IdentityModels;

public class BrandModel : ReplicationModel, IEntityHasBrand<BrandModel>
{
    public string Name { get; set; } = default!;
    public string? IntegrationId { get; set; } = default!;
    public long? BrandID { get; set; }
}
