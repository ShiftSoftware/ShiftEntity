namespace ShiftSoftware.ShiftEntity.Model.Replication;

/// <summary>
/// The key property names must exists in the ItemType class,
/// and they must be the same as container key names with the same case.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public class ReplicationPartitionKeyAttribute : Attribute
{
    public string KeyLevelOnePropertyName { get; private set; }
    public string? KeyLevelTwoPropertyName { get; private set; }
    public string? KeyLevelThreePropertyName { get; private set; }

    public ReplicationPartitionKeyAttribute(
        string keyLevelOnePropertyName,
        string? keyLevelTwoPropertyName = null,
        string? keyLevelThreePropertyName = null)
    {
        KeyLevelOnePropertyName = keyLevelOnePropertyName;
        KeyLevelTwoPropertyName = keyLevelTwoPropertyName;
        KeyLevelThreePropertyName = keyLevelThreePropertyName;
    }
}
