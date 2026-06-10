namespace ShiftSoftware.ShiftEntity.Core.Attention;

/// <summary>
/// Exposes the originating <c>AttentionHub</c> connection id for the current operation — the
/// value a client stamped on its mutating request via <see cref="AttentionRealtime.OriginHeader"/>.
/// The real-time pipeline uses it to exclude the acting window from the hint it generates
/// (<c>Clients.GroupExcept</c>), so a window is never notified about its own change.
/// </summary>
/// <remarks>
/// Defined here in <c>ShiftEntity.Core</c> — with no HTTP dependency — so the persistence layer
/// (<c>ShiftEntity.EFCore</c>) can read the origin while materializing <see cref="AttentionRaised"/>
/// events without taking a reference on ASP.NET Core. The HTTP-bound implementation lives in
/// <c>ShiftEntity.Web</c> (reads the header off <c>IHttpContextAccessor</c>) and is registered by
/// <c>AddAttentionHub()</c>. Resolve it optionally: it is absent when the real-time hub isn't
/// wired, in which case there is no origin to honour and hints go to the whole group.
/// </remarks>
public interface IAttentionOriginProvider
{
    /// <summary>
    /// The originating hub connection id for the current request, or <c>null</c> when the caller
    /// supplied none (no header) or there is no ambient request (e.g. a background save).
    /// </summary>
    string? OriginConnectionId { get; }
}
