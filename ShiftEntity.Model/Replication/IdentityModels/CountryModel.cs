using ShiftSoftware.ShiftEntity.Model.Flags;

namespace ShiftSoftware.ShiftEntity.Model.Replication.IdentityModels;

public class CountryModel : ReplicationModel, IEntityHasCountry<CountryModel>, IEntityHasRegion<CountryModel>
{
    public long? CountryID { get; set; }
    public long? RegionID { get; set; }
    public string ItemType { get; set; } = default!;

    public string Name { get; set; } = default!;
    public string? IntegrationId { get; set; }
    public string? ShortCode { get; set; }
    public string CallingCode { get; set; } = default!;
    public bool BuiltIn { get; set; }
}
