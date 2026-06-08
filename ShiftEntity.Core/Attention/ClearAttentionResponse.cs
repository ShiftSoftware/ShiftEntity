using System;

namespace ShiftSoftware.ShiftEntity.Core.Attention;

/// <summary>
/// Response body of the attention clear endpoints (<c>POST {key}/attention/clear</c> and the
/// standalone <c>POST api/attention/clear</c>).
/// </summary>
/// <remarks>
/// Clearing updates the entity row (summary columns, and the signals themselves in JSON-shadow
/// mode), which advances the entity's audit stamp — and <c>LastSaveDate</c> doubles as the
/// optimistic-concurrency version checked on update. A client that loaded the entity before
/// clearing holds the pre-clear stamp; submitting an edit with it would be rejected as a
/// version conflict. Clients should patch their loaded DTO's <c>LastSaveDate</c> with
/// <see cref="LastSaveDate"/> after a successful clear (the framework's
/// <c>ShiftEntityForm</c> does this automatically).
/// </remarks>
public sealed record ClearAttentionResponse
{
    /// <summary>
    /// The entity's <c>LastSaveDate</c> after the clear was persisted — the current
    /// optimistic-concurrency version. <c>null</c> when the entity doesn't carry audit fields.
    /// </summary>
    public DateTimeOffset? LastSaveDate { get; init; }
}
