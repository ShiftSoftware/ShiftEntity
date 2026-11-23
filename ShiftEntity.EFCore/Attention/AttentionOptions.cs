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

    /// <summary>
    /// Which saves run the evaluators. Defaults to <see cref="AttentionSaves.Every"/>: a record is flagged whatever
    /// saved it, a repository, a service or a function on the plain context.
    /// </summary>
    public AttentionSaves EvaluateOn { get; set; } = AttentionSaves.Every;
}

/// <summary>Which saves of an <see cref="Core.Attention.IHasAttention"/> row run its evaluators.</summary>
public enum AttentionSaves
{
    /// <summary>Every save of the context that adds or modifies such a row.</summary>
    Every,

    /// <summary>
    /// Only saves made through a repository (<c>ShiftRepository.SaveChangesAsync</c>), as before the evaluators moved
    /// into the context's save pipeline. A save on the plain context then raises nothing.
    /// </summary>
    Repository,
}
