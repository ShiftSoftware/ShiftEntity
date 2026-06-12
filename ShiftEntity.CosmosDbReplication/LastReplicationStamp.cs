using Microsoft.Azure.Cosmos;
using ShiftSoftware.ShiftEntity.Model.Enums;
using System.Globalization;
using System.Text.Json;

namespace ShiftSoftware.ShiftEntity.CosmosDbReplication;

/// <summary>
/// The serialized record of <b>what a row currently looks like in Cosmos</b>: the document id and all three
/// partition-key levels it was last replicated under. Persisted (as JSON) on the entity by both replication paths
/// after a successful upsert. The next sync rebuilds the current stamp and compares it against the stored one; when
/// the document id <b>or</b> any partition-key level has changed, the stale document is deleted under its OLD id +
/// key before the new one is upserted (Cosmos cannot mutate a document's id or partition key).
/// </summary>
public class LastReplicationStamp
{
    /// <summary>The Cosmos document id (the <c>id</c> property of the replicated item).</summary>
    public string? Id { get; set; }

    public PartitionKeyLevelStamp? Level1 { get; set; }
    public PartitionKeyLevelStamp? Level2 { get; set; }
    public PartitionKeyLevelStamp? Level3 { get; set; }

    /// <summary>Serialize this stamp to the JSON string stored on the entity.</summary>
    public string Serialize() => JsonSerializer.Serialize(this);

    /// <summary>
    /// Deserialize a stamp previously produced by <see cref="Serialize"/>. Returns <see langword="null"/> for a
    /// missing/blank value, any content that isn't a valid stamp, or a stamp whose levels cannot be rebuilt into a
    /// partition key (e.g. a Numeric level holding "" or garbage) — all degrade to "this row has no old stamp", so
    /// the sync skips the stale-delete, upserts, and persists a fresh correct stamp instead of throwing. The stamp
    /// is data read from a database column; one malformed row must not poison a whole sync run.
    /// </summary>
    public static LastReplicationStamp? Deserialize(string? serialized)
    {
        if (string.IsNullOrWhiteSpace(serialized))
            return null;

        try
        {
            var stamp = JsonSerializer.Deserialize<LastReplicationStamp>(serialized);

            return stamp is not null && LevelIsRebuildable(stamp.Level1) && LevelIsRebuildable(stamp.Level2)
                && LevelIsRebuildable(stamp.Level3) ? stamp : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool LevelIsRebuildable(PartitionKeyLevelStamp? level)
    {
        if (level is null || level.Value is null)
            return true; // an absent level, or a recorded null component — both rebuild fine

        return level.Type switch
        {
            PartitionKeyTypes.String => true,
            PartitionKeyTypes.Numeric => double.TryParse(level.Value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out _),
            PartitionKeyTypes.Boolean => bool.TryParse(level.Value, out _),
            _ => false, // an unknown level type cannot be rebuilt into a key component
        };
    }

    /// <summary>
    /// True when the document id or any partition-key level differs from <paramref name="other"/> — i.e. the Cosmos
    /// document this stamp describes lives under a different id/partition key than <paramref name="other"/> and must
    /// be removed before the new one is written.
    /// </summary>
    public bool DiffersFrom(LastReplicationStamp? other)
    {
        if (other is null)
            return true;

        return !string.Equals(Id, other.Id, StringComparison.Ordinal)
            || !PartitionKeyLevelStamp.AreEqual(Level1, other.Level1)
            || !PartitionKeyLevelStamp.AreEqual(Level2, other.Level2)
            || !PartitionKeyLevelStamp.AreEqual(Level3, other.Level3);
    }

    /// <summary>Rebuild the Cosmos <see cref="PartitionKey"/> from the persisted levels, for deleting the stale document.</summary>
    public PartitionKey BuildPartitionKey()
    {
        var builder = new PartitionKeyBuilder();

        AddLevel(builder, Level1);
        AddLevel(builder, Level2);
        AddLevel(builder, Level3);

        return builder.Build();
    }

    private static void AddLevel(PartitionKeyBuilder builder, PartitionKeyLevelStamp? level)
    {
        if (level is null)
            return;

        //A recorded null component rebuilds as the JSON-null key value regardless of its declared type: null is a
        //distinct partition-key value ("" / 0 / false / Undefined all address DIFFERENT partitions), and the whole
        //point of the stamp is to hit the exact key the document was stored under.
        if (level.Value is null)
        {
            builder.AddNullValue();
            return;
        }

        switch (level.Type)
        {
            case PartitionKeyTypes.String:
                builder.Add(level.Value);
                break;
            case PartitionKeyTypes.Numeric:
                builder.Add(double.Parse(level.Value, CultureInfo.InvariantCulture));
                break;
            case PartitionKeyTypes.Boolean:
                builder.Add(bool.Parse(level.Value));
                break;
        }
    }
}

/// <summary>A single partition-key level: the value (stored as an invariant string) and its Cosmos type.</summary>
public class PartitionKeyLevelStamp
{
    public string? Value { get; set; }
    public PartitionKeyTypes Type { get; set; }

    public static bool AreEqual(PartitionKeyLevelStamp? a, PartitionKeyLevelStamp? b)
    {
        if (a is null && b is null)
            return true;

        if (a is null || b is null)
            return false;

        return a.Type == b.Type && string.Equals(a.Value, b.Value, StringComparison.Ordinal);
    }
}
