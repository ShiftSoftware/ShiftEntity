using ShiftSoftware.ShiftEntity.Model.Flags;

namespace ShiftSoftware.ShiftEntity.Model.Replication.IdentityModels;

public class CityModel : ReplicationModel, 
    IEntityHasCity<CityModel>, 
    IEntityHasRegion<CityModel>, 
    IEntityHasCountry<CityModel>
{
    public string Name { get; set; } = default!;
    public string? IntegrationId { get; set; }
    public long? CountryID { get; set; } = default!;
    public long? RegionID { get; set; } = default!;
    public bool BuiltIn { get; set; }
    public string ItemType { get; set; } = default!;
    public long? CityID { get; set; }
}
