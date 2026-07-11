using Microsoft.EntityFrameworkCore;
using ShiftSoftware.ShiftEntity.Core.Attention;
using ShiftSoftware.ShiftEntity.Tests.Attention.Scenario;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Attention;

/// <summary>
/// Model configuration for the attention summary columns. Every entity that uses attention —
/// in both storage modes — gets a filtered index on <c>HasActiveAttention</c>. Aggregate
/// "needs attention" count queries can then read a nearly empty index instead of scanning
/// the whole table.
/// </summary>
public class AttentionIndexTests
{
    private static AttentionTestDbContext CreateContext()
        => new(new DbContextOptionsBuilder<AttentionTestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    [Theory]
    [InlineData(typeof(GadgetEntity))]   // JSON-shadow mode
    [InlineData(typeof(WidgetEntity))]   // indexed mode
    public void EveryAttentionEntity_GetsAFilteredIndex_OnHasActiveAttention(Type entityClrType)
    {
        using var db = CreateContext();

        var entityType = db.Model.FindEntityType(entityClrType);
        Assert.NotNull(entityType);

        var index = Assert.Single(
            entityType.GetIndexes(),
            i => i.Properties.Count == 1 && i.Properties[0].Name == nameof(IHasAttention.HasActiveAttention));

        // The filter keeps only active rows — the small set that the count badges ask about.
        Assert.Equal($"[{nameof(IHasAttention.HasActiveAttention)}] = 1", index.GetFilter());
        Assert.False(index.IsUnique);
    }
}
