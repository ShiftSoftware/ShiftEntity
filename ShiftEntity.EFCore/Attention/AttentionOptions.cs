namespace ShiftSoftware.ShiftEntity.EFCore.Attention;

/// <summary>
/// Configuration for the attention evaluation pipeline. Register via
/// <c>services.Configure&lt;AttentionOptions&gt;(o =&gt; ...)</c>.
/// </summary>
public class AttentionOptions
{
    /// <summary>
    /// Minimum interval before a signal with the same <c>(Source, Category, entityType, entityId)</c>
    /// can be raised again. Defaults to <see cref="TimeSpan.MaxValue"/> (suppress re-raise as long
    /// as an active uncleared signal exists).
    /// </summary>
    public TimeSpan ReRaiseWindow { get; set; } = TimeSpan.MaxValue;
}
