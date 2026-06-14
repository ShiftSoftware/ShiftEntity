using ShiftSoftware.ShiftEntity.Core.Attention;
using System.Threading.Channels;

namespace ShiftSoftware.ShiftEntity.EFCore.Attention;

/// <summary>
/// The framework's <see cref="IAttentionDispatcher"/>: an unbounded in-memory channel.
/// <see cref="PublishAsync"/> enqueues and returns immediately;
/// <see cref="AttentionDispatcherService"/> drains the channel on a background loop and
/// invokes the registered <see cref="IAttentionConsumer"/>s. Registered as a singleton by
/// <c>services.AddAttentionEmission()</c>.
/// </summary>
/// <remarks>
/// Delivery is in-process and best-effort (no outbox in v1): events still queued at process
/// exit are lost, which degrades to "the user sees the stored signal on next load."
/// </remarks>
internal sealed class ChannelAttentionDispatcher : IAttentionDispatcher
{
    private readonly Channel<AttentionRaised> channel = Channel.CreateUnbounded<AttentionRaised>(
        new UnboundedChannelOptions
        {
            // One drain loop (AttentionDispatcherService); many concurrent publishers (saves).
            SingleReader = true,
            SingleWriter = false,
        });

    /// <summary>The drain side, consumed by <see cref="AttentionDispatcherService"/>.</summary>
    internal ChannelReader<AttentionRaised> Reader => channel.Reader;

    /// <inheritdoc/>
    public ValueTask PublishAsync(AttentionRaised attentionRaised, CancellationToken cancellationToken = default)
    {
        // Unbounded channel: TryWrite only fails if the writer were completed, which the
        // framework never does — the drain loop stops via its own stopping token instead.
        channel.Writer.TryWrite(attentionRaised);
        return ValueTask.CompletedTask;
    }
}
