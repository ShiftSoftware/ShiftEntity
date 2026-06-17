namespace ShiftSoftware.ShiftEntity.Core.Attention;

/// <summary>
/// What an evaluator returns when it decides an entity needs attention. Return <c>null</c>
/// from an evaluator to raise nothing on this save. This is the in-memory shape produced
/// by evaluators; the framework persists it as a <see cref="StoredAttentionSignal"/>.
/// Deduplicated by <c>(Source, Category, entityType, entityId)</c> within a configurable
/// re-raise window.
/// </summary>
/// <remarks>
/// The framework persists this as a <see cref="StoredAttentionSignal"/> — into a JSON
/// shadow property for <see cref="IHasAttention"/> entities, or as a row in the universal
/// <c>AttentionSignals</c> table for <see cref="IHasIndexedAttention"/> entities. The
/// storage mode is picked by the marker interface the entity implements.
/// </remarks>
public sealed record AttentionSignal
{
    /// <summary>
    /// Categorises the signal (e.g. <c>"InvoiceOverBudget"</c>, <c>"TicketNeedsCallback"</c>).
    /// Part of the dedup key together with <see cref="Source"/>.
    /// </summary>
    public required string Category { get; init; }

    /// <summary>
    /// Identifies which evaluator raised the signal. Defaults to the evaluator's type name
    /// when left null. Part of the dedup key together with <see cref="Category"/>.
    /// </summary>
    public string? Source { get; init; }

    /// <summary>
    /// Optional human-readable explanation surfaced in the UI banner and history dialog.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>Defaults to <see cref="AttentionSeverity.Info"/> if not set.</summary>
    public AttentionSeverity Severity { get; init; } = AttentionSeverity.Info;

    /// <summary>
    /// Opaque JSON payload for evaluator-defined detail (e.g. threshold values, field names).
    /// Free-form — adding well-known keys later is backward-compatible because existing
    /// signals simply lack them.
    /// </summary>
    public string? PayloadJson { get; init; }

    /// <summary>
    /// Optional clear-scope tag that groups this signal for <em>selective</em> acknowledgment.
    /// Signals a single UI surface clears together share a scope — e.g. an evaluator that flags a
    /// chat awaiting a reply raises with <c>ClearScope = "Chat"</c> so those signals are cleared
    /// only when the chat surface is viewed, not by the form's clear-on-open. <c>null</c>/empty
    /// places the signal in the <em>default</em> scope, which clear-on-open clears. Not part of
    /// the dedup key — it governs <em>clearing</em>, not signal identity.
    /// </summary>
    public string? ClearScope { get; init; }
}
