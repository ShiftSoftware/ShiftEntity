namespace ShiftSoftware.ShiftEntity.Model.Replication.IdentityModels;

/// <summary>A named access tree, shared by its user assignments.</summary>
public class AccessTreeModel : ReplicationModel
{
    public string Name { get; set; } = default!;
    public string Tree { get; set; } = default!;
}
