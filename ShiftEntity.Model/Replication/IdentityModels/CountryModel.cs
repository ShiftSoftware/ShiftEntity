using ShiftSoftware.ShiftEntity.Model.Flags;

namespace ShiftSoftware.ShiftEntity.Model.Replication.IdentityModels;

public class CountryModel : ReplicationModel, IEntityHasCountry<CountryModel>
{
    public long? CountryID { get; set; }

    /// <summary>
    /// RegionID is only available because Country, Region, and City are stored in the same Container and RegionID is a partition key of the container.
    /// </summary>
    public long? RegionID { get; set; }
    public string ItemType { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string? IntegrationId { get; set; }
    public string? ShortCode { get; set; }
    public string CallingCode { get; set; } = default!;
    public bool BuiltIn { get; set; }
    public string? Flag { get; set; }
}
