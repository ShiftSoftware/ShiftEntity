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
    public string? Phone { get; set; }
    public string? Email { get; set; }

    /// <summary>
    /// True when the user verified <see cref="Email"/>. A document written before this field existed reads as false,
    /// which counts as not verified.
    /// </summary>
    public bool EmailVerified { get; set; }

    /// <summary>
    /// True when the user verified <see cref="Phone"/>. A document written before this field existed reads as false,
    /// which counts as not verified.
    /// </summary>
    public bool PhoneVerified { get; set; }

    public string? IntegrationId { get; set; }
    /// <summary>Null identifies an older document that does not carry authorization state.</summary>
    public bool? IsActive { get; set; }
    public string? AccessTree { get; set; }

    /// <summary>
    /// The IDs of the named access trees assigned to the user, in the order of the assignments (the user access tree
    /// rows), which is the order sign-in adds the trees to the session. An empty list means the user has no assigned
    /// tree.
    /// <para>
    /// Null means the document was written before this list existed, so the assignments are not known. A reader must
    /// not treat null as an empty list.
    /// </para>
    /// </summary>
    public List<long>? AccessTreeIDs { get; set; }

    public bool IsProtected { get; set; }
    public long? CompanyID { get; set; }
    public long? CompanyBranchID { get; set; }
    public long? RegionID { get; set; }
    public long? CountryID { get; set; }
}
