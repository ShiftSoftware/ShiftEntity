namespace ShiftSoftware.ShiftEntity.Model.Replication;

/// <summary>
/// Opt-in marker for entities whose Cosmos replication should track the document id + partition key the row was
/// last successfully replicated under. When an entity implements this, both replication paths (the after-save
/// trigger and the catch-up function) persist a serialized stamp here on every successful upsert, and use it to
/// detect an id or partition-key change and delete the stale document under its OLD id + key before upserting the
/// new one. Entities that do not implement this interface simply skip all stamp-related steps.
///
/// <para>The value is an opaque JSON string owned by the replication pipeline — a serialized
/// <c>ShiftSoftware.ShiftEntity.CosmosDbReplication.LastReplicationStamp</c>. It maps as an ordinary nullable string
/// column with no special EF configuration; the interface itself carries no Cosmos dependency, so any entity
/// project can implement it freely.</para>
/// </summary>
public interface IHasLastReplicationStamp
{
    string? LastReplicationStamp { get; set; }
}
