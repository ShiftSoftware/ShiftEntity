using System.Globalization;
using Microsoft.Azure.Cosmos;
using ShiftSoftware.ShiftEntity.CosmosDbReplication;
using ShiftSoftware.ShiftEntity.Model.Enums;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Replication;

/// <summary>
/// Unit tests for <see cref="LastReplicationStamp"/> — the model persisted on each replicated row that records the
/// Cosmos document id + partition-key levels it was last synced under. Drives the "delete the stale document under
/// its OLD id + key when either changes" behaviour used by both replication paths (trigger and catch-up function).
/// </summary>
public class LastReplicationStampTests
{
    private static PartitionKeyLevelStamp Level(string? value, PartitionKeyTypes type) => new() { Value = value, Type = type };

    private static LastReplicationStamp Stamp(string? id, PartitionKeyLevelStamp? l1 = null, PartitionKeyLevelStamp? l2 = null, PartitionKeyLevelStamp? l3 = null)
        => new() { Id = id, Level1 = l1, Level2 = l2, Level3 = l3 };

    [Fact]
    public void Serialize_Deserialize_RoundTrips_IdAndAllLevels()
    {
        var original = Stamp("doc-7",
            Level("tenant-1", PartitionKeyTypes.String),
            Level("42", PartitionKeyTypes.Numeric),
            Level("true", PartitionKeyTypes.Boolean));

        var restored = LastReplicationStamp.Deserialize(original.Serialize());

        Assert.NotNull(restored);
        Assert.Equal("doc-7", restored!.Id);
        Assert.Equal("tenant-1", restored.Level1!.Value);
        Assert.Equal(PartitionKeyTypes.String, restored.Level1.Type);
        Assert.Equal("42", restored.Level2!.Value);
        Assert.Equal(PartitionKeyTypes.Numeric, restored.Level2.Type);
        Assert.Equal("true", restored.Level3!.Value);
        Assert.Equal(PartitionKeyTypes.Boolean, restored.Level3.Type);
    }

    [Fact]
    public void DiffersFrom_IdenticalStamp_ReturnsFalse()
    {
        var a = Stamp("doc-1", Level("region-a", PartitionKeyTypes.String), Level("5", PartitionKeyTypes.Numeric));
        var b = Stamp("doc-1", Level("region-a", PartitionKeyTypes.String), Level("5", PartitionKeyTypes.Numeric));

        Assert.False(a.DiffersFrom(b));
    }

    [Fact]
    public void DiffersFrom_DifferentId_SamePartitionKey_ReturnsTrue()
    {
        // The new behaviour the requirements call out: an id change alone must be detected (and the old doc removed).
        var before = Stamp("doc-1", Level("region-a", PartitionKeyTypes.String));
        var after = Stamp("doc-2", Level("region-a", PartitionKeyTypes.String));

        Assert.True(after.DiffersFrom(before));
    }

    [Fact]
    public void DiffersFrom_DifferentPartitionKeyValue_ReturnsTrue()
    {
        var before = Stamp("doc-1", Level("region-a", PartitionKeyTypes.String));
        var after = Stamp("doc-1", Level("region-b", PartitionKeyTypes.String));

        Assert.True(after.DiffersFrom(before));
    }

    [Fact]
    public void DiffersFrom_DifferentLevelType_ReturnsTrue()
    {
        var before = Stamp("doc-1", Level("1", PartitionKeyTypes.Numeric));
        var after = Stamp("doc-1", Level("1", PartitionKeyTypes.String));

        Assert.True(after.DiffersFrom(before));
    }

    [Fact]
    public void DiffersFrom_AddedOrRemovedLevel_ReturnsTrue()
    {
        var oneLevel = Stamp("doc-1", Level("a", PartitionKeyTypes.String));
        var twoLevels = Stamp("doc-1", Level("a", PartitionKeyTypes.String), Level("b", PartitionKeyTypes.String));

        Assert.True(twoLevels.DiffersFrom(oneLevel));
        Assert.True(oneLevel.DiffersFrom(twoLevels));
    }

    [Fact]
    public void DiffersFrom_Null_ReturnsTrue()
    {
        var stamp = Stamp("doc-1", Level("a", PartitionKeyTypes.String));

        Assert.True(stamp.DiffersFrom(null));
    }

    [Fact]
    public void BuildPartitionKey_BuildsHierarchicalKeyOfAllTypes()
    {
        var stamp = Stamp("doc-1",
            Level("tenant-1", PartitionKeyTypes.String),
            Level("42", PartitionKeyTypes.Numeric),
            Level("true", PartitionKeyTypes.Boolean));

        var expected = new PartitionKeyBuilder().Add("tenant-1").Add(42d).Add(true).Build();

        Assert.Equal(expected, stamp.BuildPartitionKey());
    }

    [Fact]
    public void BuildPartitionKey_NumericLevel_UsesInvariantCultureUnderOtherCultures()
    {
        // Guards the InvariantCulture handling: under a culture using ',' as the decimal separator a naive
        // double.Parse("5.5") would misread the value. The expected key is built from the double directly.
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            var stamp = Stamp("doc-1", Level("5.5", PartitionKeyTypes.Numeric));
            var expected = new PartitionKeyBuilder().Add(5.5d).Build();

            Assert.Equal(expected, stamp.BuildPartitionKey());
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-json")]
    [InlineData("[{\"Value\":\"x\"}]")] // wrong shape (a JSON array, not a stamp object)
    public void Deserialize_MissingOrInvalid_ReturnsNull(string? serialized)
    {
        Assert.Null(LastReplicationStamp.Deserialize(serialized));
    }
}
