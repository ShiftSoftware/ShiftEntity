using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Notifications;
using System.Globalization;
using System.Threading.Channels;

namespace ShiftSoftware.ShiftEntity.EFCore.Notifications;

/// <summary>An event waiting in the queue, with everything captured when it was raised.</summary>
internal abstract class NotificationRaised
{
    public required NotificationTopic Topic { get; init; }
    public required long RecordID { get; init; }
    public required IReadOnlyList<NotificationWatcher> Watchers { get; init; }
    public required IReadOnlySet<long> Except { get; init; }
    public required CultureInfo Culture { get; init; }
    public required CultureInfo UICulture { get; init; }

    public abstract string EntityTypeName { get; }

    public abstract Task ProcessAsync(IServiceProvider scope, CancellationToken cancellationToken);
}

internal sealed class NotificationRaised<TEntity> : NotificationRaised
    where TEntity : ShiftEntity<TEntity>, new()
{
    public override string EntityTypeName => typeof(TEntity).Name;

    public override Task ProcessAsync(IServiceProvider scope, CancellationToken cancellationToken)
        => scope.GetRequiredService<NotificationPipeline>().ProcessAsync(this, cancellationToken);
}

/// <summary>
/// An unbounded in-memory channel, with its own queue apart from attention's. Adding returns at once, so a save never
/// waits for the pipeline. Anything still queued when the process stops is lost.
/// </summary>
internal sealed class NotificationQueue
{
    private readonly Channel<NotificationRaised> channel = Channel.CreateUnbounded<NotificationRaised>(
        new UnboundedChannelOptions
        {
            // One background reader. Many saves write at the same time.
            SingleReader = true,
            SingleWriter = false,
        });

    public ChannelReader<NotificationRaised> Reader => channel.Reader;

    // TryWrite fails only when the writer is completed, and nothing ever completes it. The reader stops through its own
    // stopping token.
    public void Enqueue(NotificationRaised raised) => channel.Writer.TryWrite(raised);
}

/// <summary>
/// Reads the queue one event at a time, in the order they were raised. Each event gets a fresh DI scope. A failure is
/// logged and doesn't affect the next event.
/// </summary>
internal sealed class NotificationDispatcherService : BackgroundService
{
    private readonly NotificationQueue queue;
    private readonly IServiceScopeFactory scopeFactory;
    private readonly NotificationOptions options;
    private readonly ILogger<NotificationDispatcherService> logger;

    public NotificationDispatcherService(
        NotificationQueue queue,
        IServiceScopeFactory scopeFactory,
        NotificationOptions options,
        ILogger<NotificationDispatcherService> logger)
    {
        this.queue = queue;
        this.scopeFactory = scopeFactory;
        this.options = options;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var raised in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(options.EventTimeout);

                await using var scope = scopeFactory.CreateAsyncScope();

                // WaitAsync gives up even on a sender that ignores the token, so one hung event can't stop the queue.
                await raised.ProcessAsync(scope.ServiceProvider, timeout.Token).WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // The host is shutting down. Stop reading. The events still queued are lost.
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(exception,
                    "Notification failed for topic {Topic} on {EntityType} {RecordID}. Nothing more is sent for this event, and the next event carries on.",
                    raised.Topic.ID, raised.EntityTypeName, raised.RecordID);
            }
        }
    }
}
