namespace ShiftSoftware.ShiftEntity.Core.Attention;

/// <summary>
/// What a real-time <see cref="AttentionRealtimePayload"/> represents: a signal was newly
/// <see cref="Raised"/>, or active signals were <see cref="Cleared"/> (acknowledged). Both are
/// refresh hints — a subscribed list/form re-reads on either so its indicator appears or drops —
/// but only a raise is toast-worthy. A clear silently drops the indicator; surfacing a snackbar
/// for an acknowledgement someone else performed is noise, so the client suppresses the toast
/// when <see cref="Cleared"/>.
/// </summary>
public enum AttentionRealtimeKind
{
    /// <summary>A new signal was raised on the entity.</summary>
    Raised,

    /// <summary>The entity's active signals were cleared (acknowledged).</summary>
    Cleared,
}
