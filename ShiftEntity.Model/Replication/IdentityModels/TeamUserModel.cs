namespace ShiftSoftware.ShiftEntity.Model.Replication.IdentityModels;

/// <summary>A membership used by the user's TeamIds claims. The document id is the SQL join row id.</summary>
public class TeamUserModel : ReplicationModel
{
    public long UserID { get; set; }
    public long TeamID { get; set; }
}
