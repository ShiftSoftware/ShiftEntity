using ShiftSoftware.ShiftEntity.Model.Enums;
using ShiftSoftware.ShiftEntity.Model.Flags;

namespace ShiftSoftware.ShiftEntity.Model.Replication.IdentityModels;

public class CompanyModel : ReplicationModel, IEntityHasCompany<CompanyModel>
{
    public string Name { get; set; } = default!;
    public string? LegalName { get; set; }
    public string? IntegrationId { get; set; }
    public string? ShortCode { get; set; }
    public CompanyTypes CompanyType { get; set; }
    public string? Logo { get; set; }
    public string? HQPhone { get; set; }
    public string? HQEmail { get; set; }
    public string? HQAddress { get; set; }
    public string? Website { get; set; }
    public bool BuiltIn { get; set; }
    public DateTime? TerminationDate { get; set; }
    public Dictionary<string, CustomField>? CustomFields { get; set; }
    public long? ParentCompanyID { get; set; }
    public long? CompanyID { get; set; }
    public int? DisplayOrder { get; set; }
}
