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

    // ── Null partition-key components (the lossy-mapping shape) ───────────────
    //
    // A consumer's mapping may leave a partition-key component unset (e.g. it never assigns ItemType). The stamp is
    // the record of what was ACTUALLY written to Cosmos, so it must represent, round-trip, compare, and rebuild null
    // components faithfully — the stale-document delete targets these exact coordinates.

    [Fact]
    public void Serialize_Deserialize_RoundTrips_NullLevelValue()
    {
        var original = Stamp("doc-1", Level("1", PartitionKeyTypes.Numeric), Level(null, PartitionKeyTypes.String));

        var restored = LastReplicationStamp.Deserialize(original.Serialize());

        Assert.NotNull(restored);
        Assert.Null(restored!.Level2!.Value);
        Assert.Equal(PartitionKeyTypes.String, restored.Level2.Type);
        Assert.False(restored.DiffersFrom(original));
    }

    [Fact]
    public void DiffersFrom_NullVersusValue_OnALevel_ReturnsTrue()
    {
        // The stored document was written with ItemType "Region"; the mapping then stopped setting it (null).
        // The stamps must compare as different, so the old document gets deleted under its STORED coordinates
        // instead of being orphaned by a delete aimed at a reconstructed (and wrong) key.
        var stored = Stamp("doc-1", Level("1", PartitionKeyTypes.Numeric), Level("Region", PartitionKeyTypes.String));
        var current = Stamp("doc-1", Level("1", PartitionKeyTypes.Numeric), Level(null, PartitionKeyTypes.String));

        Assert.True(current.DiffersFrom(stored));
        Assert.True(stored.DiffersFrom(current));
    }

    [Fact]
    public void BuildPartitionKey_NullStringLevel_BuildsNullComponent()
    {
        // Deleting a document that was stored under a null component must target exactly that key. Verified against
        // a real Cosmos container: a document whose key path holds JSON null is HIT by a key built with a null
        // component and MISSED by "", 0, false, or Undefined — they are all distinct partition-key values.
        var stamp = Stamp("doc-1", Level("5", PartitionKeyTypes.Numeric), Level(null, PartitionKeyTypes.String));

        var expected = new PartitionKeyBuilder().Add(5d).AddNullValue().Build();

        Assert.Equal(expected, stamp.BuildPartitionKey());
    }

    [Fact]
    public void BuildPartitionKey_NullNumericLevel_BuildsNullComponent_InsteadOfThrowing()
    {
        // A nullable numeric key column (e.g. a CountryID that is null for some rows) must rebuild as the JSON-null
        // component — double.Parse(null) used to throw here, killing the sync of any such row.
        var stamp = Stamp("doc-1", Level(null, PartitionKeyTypes.Numeric), Level("2", PartitionKeyTypes.Numeric));

        var expected = new PartitionKeyBuilder().AddNullValue().Add(2d).Build();

        Assert.Equal(expected, stamp.BuildPartitionKey());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-number")]
    public void Deserialize_NumericLevelWithUnrebuildableValue_ReturnsNull(string value)
    {
        // A stamp is data read from a database column. Builds prior to the null-component fix recorded null
        // numeric components as "" (e.g. a Country row's null RegionID), and BuildPartitionKey would throw on
        // them, poisoning the whole sync run. Such content must instead degrade to "no old stamp": the sync skips
        // the stale-delete, upserts, and persists a fresh correct stamp — self-healing.
        var serialized = Stamp("doc-1",
            Level("1", PartitionKeyTypes.Numeric),
            Level(value, PartitionKeyTypes.Numeric)).Serialize();

        Assert.Null(LastReplicationStamp.Deserialize(serialized));
    }

    [Fact]
    public void Deserialize_BooleanLevelWithUnrebuildableValue_ReturnsNull()
    {
        var serialized = Stamp("doc-1", Level("maybe", PartitionKeyTypes.Boolean)).Serialize();

        Assert.Null(LastReplicationStamp.Deserialize(serialized));
    }

    [Fact]
    public void Deserialize_EmptyStringOnAStringLevel_IsValid()
    {
        // "" is a legitimate STRING key component (only numeric/boolean levels can't rebuild from it), so these
        // stamps stay usable — the ""-vs-null difference is what drives their self-healing delete-and-rewrite.
        var serialized = Stamp("doc-1", Level("1", PartitionKeyTypes.Numeric), Level("", PartitionKeyTypes.String)).Serialize();

        var restored = LastReplicationStamp.Deserialize(serialized);

        Assert.NotNull(restored);
        Assert.Equal("", restored!.Level2!.Value);
    }

    [Fact]
    public void DiffersFrom_EmptyStringVersusNull_OnALevel_ReturnsTrue()
    {
        // "" and null are DIFFERENT partition-key values. This also self-heals stamps recorded by builds that
        // coerced null components to "" (Convert.ToString(null)): the first sync after the fix sees the stamps
        // differ, attempts the stale delete (a swallowed 404 at the ""-key), upserts in place, and persists a
        // correct null-component stamp.
        var corrupt = Stamp("doc-1", Level("1", PartitionKeyTypes.Numeric), Level("", PartitionKeyTypes.String));
        var correct = Stamp("doc-1", Level("1", PartitionKeyTypes.Numeric), Level(null, PartitionKeyTypes.String));

        Assert.True(correct.DiffersFrom(corrupt));
    }
}
