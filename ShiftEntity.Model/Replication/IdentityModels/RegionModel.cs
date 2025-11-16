using ShiftSoftware.ShiftEntity.Model.Flags;
using ShiftSoftware.ShiftEntity.Model.Replication;

namespace ShiftSoftware.ShiftEntity.Model.Replication.IdentityModels;

public class RegionModel : ReplicationModel, IEntityHasRegion<RegionModel>, IEntityHasCountry<RegionModel>
{
    public long? CountryID { get; set; } = default!;
    public long? RegionID { get; set; }
    public string Name { get; set; } = default!;
    public string? IntegrationId { get; set; } = default!;
    public string? ShortCode { get; set; }
    public bool BuiltIn { get; set; }
    public string ItemType { get; set; } = default!;
    public string? Flag { get; set; }
}
