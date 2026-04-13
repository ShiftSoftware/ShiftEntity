using ShiftSoftware.ShiftEntity.Model.Flags;

namespace ShiftSoftware.ShiftEntity.Model.Replication.IdentityModels;

public class UserModel : 
    ReplicationModel, 
    IEntityHasCompany<UserModel>, 
    IEntityHasCompanyBranch<UserModel>,
    IEntityHasRegion<UserModel>,
    IEntityHasCountry<UserModel>
{
    public string FullName { get; set; } = default!;
    public string Username { get; set; } = default!;
    public string? IntegrationId { get; set; }
    public bool BuiltIn { get; set; }
    public long? CompanyID { get; set; }
    public long? CompanyBranchID { get; set; }
    public long? RegionID { get; set; }
    public long? CountryID { get; set; }
}