namespace ShiftSoftware.ShiftEntity.Model.Replication.IdentityModels;

public class CountryModel : ReplicationModel
{
    public string CountryID { get; set; } = default!;
    public string? RegionID { get; set; }
    public string ItemType { get; set; } = default!;

    public string Name { get; set; } = default!;
    public string? IntegrationId { get; set; }
    public string? ShortCode { get; set; }
    public string CallingCode { get; set; } = default!;
    public bool BuiltIn { get; set; }
}
