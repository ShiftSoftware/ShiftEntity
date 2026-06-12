using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.CosmosDbReplication;
using ShiftSoftware.ShiftEntity.Model.Replication;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Replication;

/// <summary>
/// Pins the semantics of <see cref="IShiftEntityReplication.LastReplicationDate"/>: despite the name, it is a
/// replication <b>watermark</b> — the save-date of the row version that reached Cosmos — never the wall-clock time
/// replication ran. Both sync paths decide "dirty" via <c>LastReplicationDate &lt; LastSaveDate</c>, so the
/// watermark must come from the loaded version's <c>LastSaveDate</c> for that comparison to survive concurrent
/// edits. The single write-side is <c>MarkReplicated</c>, which stamps the watermark and the stamp together.
/// </summary>
public class ReplicationWatermarkTests
{
    private sealed class WatermarkEntity : ShiftEntity<WatermarkEntity>, IShiftEntityReplication
    {
        public DateTimeOffset? LastReplicationDate { get; set; }
        public string? LastReplicationStamp { get; set; }
    }

    [Fact]
    public void MarkReplicated_CopiesLastSaveDate_NeverTheCurrentTime()
    {
        // Guard rail: do NOT change MarkReplicated to stamp DateTimeOffset.UtcNow. The feature's first
        // version did exactly that (fixed in 815f606, 2023-09): stamping "now" marks a row clean even when a user
        // saved a newer version while the sync was in flight, so that edit would never reach Cosmos (see the race
        // test below).
        var savedAt = new DateTimeOffset(2024, 3, 1, 10, 0, 0, TimeSpan.Zero); // deliberately far from "now"
        var entity = new WatermarkEntity { LastSaveDate = savedAt };

        entity.MarkReplicated("""{"ID":"1"}""");

        Assert.Equal(savedAt, entity.LastReplicationDate);
    }

    [Fact]
    public void SaveDuringReplication_LeavesTheRowDue_ForTheNextSync()
    {
        // The lost-update race the watermark exists for: replication loads version T, a user saves a newer version
        // while the push to Cosmos is in flight, and the bookkeeping write lands last. Because the watermark is T
        // (not "now"), the dirty-check still selects the row and the user's edit replicates on the next sync.
        var loadedVersion = new DateTimeOffset(2024, 3, 1, 10, 0, 0, TimeSpan.Zero);
        var entity = new WatermarkEntity { LastSaveDate = loadedVersion };

        entity.MarkReplicated("""{"ID":"1"}""");                // replication of version T completes
        entity.LastSaveDate = loadedVersion.AddMinutes(4);      // the user saved while the sync was in flight

        Assert.True(entity.LastReplicationDate < entity.LastSaveDate); // still dirty — the edit is not lost
    }

    [Fact]
    public void MarkReplicated_ExactEquality_MeansInSync()
    {
        // The flip side of the watermark design: equality with LastSaveDate IS the definition of "clean". (This is
        // why the two columns always show identical values in the database for in-sync rows.)
        var entity = new WatermarkEntity { LastSaveDate = new DateTimeOffset(2024, 3, 1, 10, 0, 0, TimeSpan.Zero) };

        entity.MarkReplicated("""{"ID":"1"}""");

        Assert.True(entity.LastReplicationDate.HasValue);
        Assert.False(entity.LastReplicationDate < entity.LastSaveDate);
    }

    [Fact]
    public void MarkReplicated_WritesTheWatermarkAndTheStampTogether()
    {
        // The pair is the whole point of the consolidated write-side: a successful sync records "which version
        // reached Cosmos" (watermark) and "under which coordinates" (stamp) in one call, so they cannot drift.
        var savedAt = new DateTimeOffset(2024, 3, 1, 10, 0, 0, TimeSpan.Zero);
        var entity = new WatermarkEntity { LastSaveDate = savedAt };

        entity.MarkReplicated("""{"ID":"42"}""");

        Assert.Equal(savedAt, entity.LastReplicationDate);
        Assert.Equal("""{"ID":"42"}""", entity.LastReplicationStamp);
    }

    [Fact]
    public void ReplicationColumns_AreOptIn_AndTravelAsAPair()
    {
        // The watermark and the stamp are written by the same pipeline and only mean anything together, so they
        // live as a pair on IShiftEntityReplication — which replication setup requires — and the base entity
        // carries neither. Guard rail: do not move either column back onto ShiftEntity, where every table would
        // get it regardless of replication.
        Assert.Null(typeof(ShiftEntity<>).GetProperty("LastReplicationDate"));
        Assert.Null(typeof(ShiftEntity<>).GetProperty("LastReplicationStamp"));
    }
}
