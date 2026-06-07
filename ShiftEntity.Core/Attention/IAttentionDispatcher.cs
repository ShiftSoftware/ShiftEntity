using System.Threading;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Core.Attention;

/// <summary>
/// Routes <see cref="AttentionRaised"/> events from the save pipeline to the registered
/// <see cref="IAttentionConsumer"/>s. The framework publishes through this automatically
/// after each committed save that raised signals — application code normally only
/// <em>registers</em> the dispatcher (<c>services.AddAttentionEmission()</c>) and consumers
/// (<c>services.AddAttentionConsumer&lt;T&gt;()</c>), and never calls it directly.
/// </summary>
/// <remarks>
/// The framework implementation enqueues onto an in-memory channel drained by a background
/// service, so publishing is non-blocking: save latency is unaffected by consumer latency.
/// When no dispatcher is registered, the save pipeline skips publishing entirely — apps
/// that don't opt in see no behavior change. Publishing directly (e.g. from a future
/// scheduler that runs time-based evaluators outside the save pipeline) is supported;
/// events flow to consumers identically.
/// </remarks>
public interface IAttentionDispatcher
{
    /// <summary>
    /// Enqueues one event for delivery to all registered <see cref="IAttentionConsumer"/>s.
    /// Returns as soon as the event is accepted — consumers run asynchronously. During host
    /// shutdown the event may be dropped (best-effort, in-process delivery).
    /// </summary>
    ValueTask PublishAsync(AttentionRaised attentionRaised, CancellationToken cancellationToken = default);
}
