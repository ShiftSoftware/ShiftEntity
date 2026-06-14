using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ShiftSoftware.ShiftEntity.Core.Attention;

namespace ShiftSoftware.ShiftEntity.EFCore.Attention;

/// <summary>
/// Background drain loop for <see cref="ChannelAttentionDispatcher"/>. Processes events
/// sequentially in raise order; each event gets a fresh DI scope from which all registered
/// <see cref="IAttentionConsumer"/>s are resolved and invoked. Registered by
/// <c>services.AddAttentionEmission()</c>.
/// </summary>
/// <remarks>
/// A consumer that throws is logged and isolated: the remaining consumers still run for
/// that event, and subsequent events are unaffected. Host shutdown stops the loop via the
/// stopping token; events still queued are dropped (best-effort, in-process delivery).
/// </remarks>
internal sealed class AttentionDispatcherService : BackgroundService
{
    private readonly ChannelAttentionDispatcher dispatcher;
    private readonly IServiceScopeFactory scopeFactory;
    private readonly ILogger<AttentionDispatcherService> logger;

    public AttentionDispatcherService(
        ChannelAttentionDispatcher dispatcher,
        IServiceScopeFactory scopeFactory,
        ILogger<AttentionDispatcherService> logger)
    {
        this.dispatcher = dispatcher;
        this.scopeFactory = scopeFactory;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var attentionRaised in dispatcher.Reader.ReadAllAsync(stoppingToken))
        {
            using var scope = scopeFactory.CreateScope();

            foreach (var consumer in scope.ServiceProvider.GetServices<IAttentionConsumer>())
            {
                try
                {
                    await consumer.HandleAsync(attentionRaised, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // Host shutdown mid-consumer — stop draining; BackgroundService treats
                    // cancellation via the stopping token as a graceful stop.
                    throw;
                }
                catch (Exception exception)
                {
                    logger.LogError(
                        exception,
                        "Attention consumer {Consumer} failed handling {EntityType}/{EntityId} ({Source}/{Category}). " +
                        "The event continues to the remaining consumers.",
                        consumer.GetType().Name,
                        attentionRaised.EntityType,
                        attentionRaised.EntityId,
                        attentionRaised.Signal.Source,
                        attentionRaised.Signal.Category);
                }
            }
        }
    }
}
