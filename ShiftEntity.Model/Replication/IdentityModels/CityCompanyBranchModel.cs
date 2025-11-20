using ShiftSoftware.ShiftEntity.Model.Replication;

namespace ShiftSoftware.ShiftEntity.Model.Replication.IdentityModels;

public class CityCompanyBranchModel : ReplicationModel
{
    public string Name { get; set; } = default!;
    public string? IntegrationId { get; set; }
    public bool BuiltIn { get; set; }
    public int? DisplayOrder { get; set; }
    public CityRegionModel Region { get; set; }
}
