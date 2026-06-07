using System.Threading;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Core.Attention;

/// <summary>
/// A handler for <see cref="AttentionRaised"/> events — email senders, audit writers,
/// real-time notifiers, etc. Register with
/// <c>services.AddAttentionConsumer&lt;MyConsumer&gt;()</c>; all registered consumers
/// receive every published event.
/// </summary>
/// <remarks>
/// <para>
/// Consumers run on a background drain loop, not on the request that performed the save:
/// events are processed sequentially in raise order, each in a fresh DI scope (constructor
/// dependencies behave as they would in a request scope). A consumer that throws is logged
/// and isolated — it never affects the save (already committed), the other consumers, or
/// subsequent events.
/// </para>
/// <para>
/// Delivery is in-process and best-effort: an event accepted before a process crash or
/// shutdown may never reach consumers (there is no outbox in v1). Consumers should treat a
/// missed event as "the user sees the signal on next load" — the stored signal itself is
/// never lost, only this notification of it.
/// </para>
/// </remarks>
public interface IAttentionConsumer
{
    /// <summary>
    /// Handles one published event. <paramref name="cancellationToken"/> signals host
    /// shutdown — long-running work should observe it.
    /// </summary>
    Task HandleAsync(AttentionRaised attentionRaised, CancellationToken cancellationToken);
}
