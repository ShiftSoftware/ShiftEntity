using System.Collections.Generic;
using System.Linq;

namespace ShiftSoftware.ShiftEntity.Core.Attention;

/// <summary>
/// Selects which of an entity's active attention signals a clear operation affects. The clear
/// pipeline marks every active signal the filter <see cref="Matches"/> as cleared and leaves the
/// rest active, recomputing the entity's summary columns from whatever remains.
/// </summary>
/// <remarks>
/// <para>
/// A <c>null</c> filter — or <see cref="All"/> — clears every active signal, the historical
/// all-or-nothing behavior. The three selection modes are mutually exclusive in practice:
/// </para>
/// <list type="bullet">
///   <item><description><see cref="Signal"/> — one signal by its dedup key <c>(Source, Category)</c>,
///   which is unique among an entity's active signals. Used by the banner's per-signal dismiss.</description></item>
///   <item><description><see cref="ByScope"/> — every active signal in the given
///   <see cref="StoredAttentionSignal.ClearScope"/> bucket(s). Used by a surface that owns a scope
///   (e.g. a chat tab clearing <c>"Chat"</c>). Pass <c>""</c> for the default/unscoped bucket.</description></item>
///   <item><description><see cref="All"/> — no restriction.</description></item>
/// </list>
/// </remarks>
public sealed record AttentionClearFilter
{
    /// <summary>
    /// Clear only signals whose <see cref="StoredAttentionSignal.ClearScope"/> is in this set. A
    /// signal with a <c>null</c>/empty scope matches the empty-string entry <c>""</c> (the default
    /// scope). Ignored when <see cref="Source"/> and <see cref="Category"/> identify a single signal.
    /// </summary>
    public IReadOnlyCollection<string>? Scopes { get; init; }

    /// <summary>Clear only the single signal with this source (paired with <see cref="Category"/>).</summary>
    public string? Source { get; init; }

    /// <summary>Clear only the single signal with this category (paired with <see cref="Source"/>).</summary>
    public string? Category { get; init; }

    /// <summary>Clears every active signal — the default when no filter is supplied.</summary>
    public static AttentionClearFilter All { get; } = new();

    /// <summary>Clears the default (unscoped) bucket only — signals with a null/empty scope.</summary>
    public static AttentionClearFilter DefaultScope { get; } = new() { Scopes = new[] { "" } };

    /// <summary>Clears every active signal in the given scope(s). Pass <c>""</c> for the default bucket.</summary>
    public static AttentionClearFilter ByScope(params string[] scopes) => new() { Scopes = scopes };

    /// <summary>Clears the single signal identified by its dedup key.</summary>
    public static AttentionClearFilter Signal(string source, string category)
        => new() { Source = source, Category = category };

    /// <summary>Whether this filter selects <paramref name="signal"/> for clearing.</summary>
    public bool Matches(StoredAttentionSignal signal)
    {
        // Per-signal (dedup-key) selection wins over a scope restriction.
        if (Source is not null && Category is not null)
            return signal.Source == Source && signal.Category == Category;

        if (Scopes is { Count: > 0 })
            return Scopes.Contains(signal.ClearScope ?? string.Empty);

        return true; // no restriction → all active signals
    }
}
