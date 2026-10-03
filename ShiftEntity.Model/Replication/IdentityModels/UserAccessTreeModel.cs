namespace ShiftSoftware.ShiftEntity.Model.Replication.IdentityModels;

/// <summary>A user assignment. The document id is the SQL join row id.</summary>
public class UserAccessTreeModel : ReplicationModel
{
    public long UserID { get; set; }
    public long AccessTreeID { get; set; }
}
