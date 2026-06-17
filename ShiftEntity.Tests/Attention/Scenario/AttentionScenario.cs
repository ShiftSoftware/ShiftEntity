using Microsoft.EntityFrameworkCore;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Attention;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.Model.Dtos;

namespace ShiftSoftware.ShiftEntity.Tests.Attention.Scenario;

/// <summary>EF context for the attention emission tests — one entity per storage mode.</summary>
public class AttentionTestDbContext : ShiftDbContext
{
    public DbSet<GadgetEntity> Gadgets { get; set; } = default!;
    public DbSet<WidgetEntity> Widgets { get; set; } = default!;

    public AttentionTestDbContext(DbContextOptions<AttentionTestDbContext> options) : base(options) { }
}

/// <summary>
/// JSON-shadow-mode entity (<see cref="IHasAttention"/>) with a self-evaluator. The rule is
/// deliberately <em>state-based</em> (raises whenever stock is low, not only on the
/// transition into low) so the dedup tests can prove that a raw signal suppressed by the
/// framework's <c>(Source, Category)</c> dedup publishes no event.
/// </summary>
public class GadgetEntity : ShiftEntity<GadgetEntity>, IHasAttention, IHasAttentionEvaluator<GadgetEntity>
{
    public const string LowStockCategory = "LowStock";

    public string Name { get; set; } = "";
    public int StockLevel { get; set; } = 10;

    public bool HasActiveAttention { get; set; }
    public AttentionSeverity? HighestSeverity { get; set; }
    public int ActiveSignalCount { get; set; }

    public AttentionSignal? EvaluateAttention(AttentionContext<GadgetEntity> context)
        => context.Entity.StockLevel < 5
            ? new AttentionSignal
            {
                Category = LowStockCategory,
                Reason = $"Stock is down to {context.Entity.StockLevel}",
                Severity = AttentionSeverity.Warning,
            }
            : null;
}

/// <summary>
/// Indexed-mode entity (<see cref="IHasIndexedAttention"/>) — forces the repository's
/// transactional save path (<c>_needsAttentionTransaction</c>) and, on Insert, the
/// pending-signal flush that only knows the entity ID after the first
/// <c>SaveChangesAsync</c>. Evaluated by the DI-registered <see cref="WidgetFlagEvaluator"/>.
/// </summary>
public class WidgetEntity : ShiftEntity<WidgetEntity>, IHasIndexedAttention
{
    public string Name { get; set; } = "";
    public bool Flagged { get; set; }

    public bool HasActiveAttention { get; set; }
    public AttentionSeverity? HighestSeverity { get; set; }
    public int ActiveSignalCount { get; set; }
}

/// <summary>
/// DI-registered evaluator for <see cref="WidgetEntity"/> — the second evaluator discovery
/// source (the gadget's self-evaluator is the first), proving emission is agnostic to how
/// the signal was produced. State-based for the same dedup-test reason as the gadget rule.
/// </summary>
public sealed class WidgetFlagEvaluator : IAttentionEvaluator<WidgetEntity>
{
    public const string FlaggedCategory = "Flagged";

    public AttentionSignal? Evaluate(AttentionContext<WidgetEntity> context)
        => context.Entity.Flagged
            ? new AttentionSignal
            {
                Category = FlaggedCategory,
                Reason = "Widget was flagged",
                Severity = AttentionSeverity.Critical,
            }
            : null;
}

/// <summary>
/// DI evaluator that raises a <em>scoped</em> signal (<see cref="ReviewScope"/>) alongside the
/// gadget's own default-scope LowStock signal — so the scoped-clear tests have one entity carrying
/// two signals in different clear scopes. Co-fires with LowStock (<c>StockLevel &lt; 5</c>).
/// </summary>
public sealed class GadgetReviewEvaluator : IAttentionEvaluator<GadgetEntity>
{
    public const string ReviewCategory = "NeedsReview";
    public const string ReviewScope = "Review";

    public AttentionSignal? Evaluate(AttentionContext<GadgetEntity> context)
        => context.Entity.StockLevel < 5
            ? new AttentionSignal
            {
                Category = ReviewCategory,
                Reason = "Low-stock gadget needs review",
                Severity = AttentionSeverity.Info,
                ClearScope = ReviewScope,
            }
            : null;
}

/// <summary>Indexed-mode counterpart of <see cref="GadgetReviewEvaluator"/>, for <see cref="WidgetEntity"/>.</summary>
public sealed class WidgetReviewEvaluator : IAttentionEvaluator<WidgetEntity>
{
    public const string ReviewCategory = "NeedsReview";
    public const string ReviewScope = "Review";

    public AttentionSignal? Evaluate(AttentionContext<WidgetEntity> context)
        => context.Entity.Flagged
            ? new AttentionSignal
            {
                Category = ReviewCategory,
                Reason = "Flagged widget needs review",
                Severity = AttentionSeverity.Info,
                ClearScope = ReviewScope,
            }
            : null;
}

/// <summary>Minimal DTO — the repository's type parameters demand one; emission tests never map.</summary>
public class GadgetDTO : ShiftEntityDTOBase
{
    public override string? ID { get; set; }
    public string Name { get; set; } = "";
}

/// <inheritdoc cref="GadgetDTO"/>
public class WidgetDTO : ShiftEntityDTOBase
{
    public override string? ID { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>
/// Mapper double for the emission tests — every member throws. The tests drive saves by
/// adding/mutating tracked entities directly and calling <c>SaveChangesAsync</c>, which
/// never maps (no <c>ReloadAfterSave</c> entities in the scenario), so a mapper call is a
/// failure worth surfacing loudly.
/// </summary>
public sealed class ThrowingGadgetMapper : IShiftEntityMapper<GadgetEntity, GadgetDTO, GadgetDTO>
{
    public GadgetDTO MapToView(GadgetEntity entity) => throw new NotSupportedException();
    public GadgetEntity MapToEntity(GadgetDTO dto, GadgetEntity existing) => throw new NotSupportedException();
    public IQueryable<GadgetDTO> MapToList(IQueryable<GadgetEntity> query) => throw new NotSupportedException();
    public void CopyEntity(GadgetEntity source, GadgetEntity target) => throw new NotSupportedException();
}

/// <inheritdoc cref="ThrowingGadgetMapper"/>
public sealed class ThrowingWidgetMapper : IShiftEntityMapper<WidgetEntity, WidgetDTO, WidgetDTO>
{
    public WidgetDTO MapToView(WidgetEntity entity) => throw new NotSupportedException();
    public WidgetEntity MapToEntity(WidgetDTO dto, WidgetEntity existing) => throw new NotSupportedException();
    public IQueryable<WidgetDTO> MapToList(IQueryable<WidgetEntity> query) => throw new NotSupportedException();
    public void CopyEntity(WidgetEntity source, WidgetEntity target) => throw new NotSupportedException();
}
