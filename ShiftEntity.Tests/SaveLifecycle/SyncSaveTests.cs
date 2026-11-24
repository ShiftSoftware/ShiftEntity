using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.Tests.SaveHandlers;
using Xunit;
using static ShiftSoftware.ShiftEntity.Tests.SaveHandlers.SavePipelineModel;

namespace ShiftSoftware.ShiftEntity.Tests.SaveLifecycle;

/// <summary>
/// The synchronous calls share the asynchronous path: <c>SaveChanges()</c> and <c>Commit()</c> run every stage, and
/// wait for what the handlers do asynchronously. While they wait, the caller's synchronization context is cleared, so
/// a handler that awaits without <c>ConfigureAwait(false)</c> cannot deadlock against the thread that waits.
/// </summary>
public class SyncSaveTests
{
    // Runs what is posted to it on one thread, one piece at a time, as a UI thread or a renderer does.
    private sealed class SingleThread : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> queue = new();
        private readonly Thread thread;

        public SingleThread()
        {
            thread = new Thread(() =>
            {
                SetSynchronizationContext(this);

                foreach (var (callback, state) in queue.GetConsumingEnumerable())
                    callback(state);
            })
            { IsBackground = true };

            thread.Start();
        }

        public override void Post(SendOrPostCallback d, object? state) => queue.Add((d, state));

        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

        /// <summary>Runs the work on the one thread, and gives up when it has not finished in time.</summary>
        public T Run<T>(Func<T> work)
        {
            var done = new TaskCompletionSource<T>();

            Post(_ =>
            {
                try
                {
                    done.SetResult(work());
                }
                catch (Exception exception)
                {
                    done.SetException(exception);
                }
            }, null);

            if (!done.Task.Wait(TimeSpan.FromSeconds(20)))
                throw new TimeoutException("The synchronous call never returned: its handler waits for the thread that waits for it.");

            return done.Task.GetAwaiter().GetResult();
        }

        public void Dispose() => queue.CompleteAdding();
    }

    // Awaits the way app code does, without ConfigureAwait(false), in every stage.
    public sealed class AwaitsInEveryStage(SaveLog log)
        : IAfterSaveHandler<Order>, IBeforeCommitHandler<Order>, IAfterCommitHandler<Order>
    {
        public async ValueTask AfterSaveAsync(SavedRows<Order> rows, CancellationToken cancellationToken)
        {
            await Task.Delay(20, cancellationToken);
            await rows.Db.Set<Memo>().CountAsync(cancellationToken);
            log.Add("after save");
        }

        public async ValueTask BeforeCommitAsync(SavedRows<Order> rows, CancellationToken cancellationToken)
        {
            await Task.Delay(20, cancellationToken);
            log.Add("before commit");
        }

        public async ValueTask AfterCommitAsync(SavedRows<Order> rows, CancellationToken cancellationToken)
        {
            await Task.Yield();
            await Task.Delay(20, cancellationToken);
            log.Add("after commit");
        }
    }

    [Fact]
    public async Task A_synchronous_save_under_a_single_threaded_synchronization_context_does_not_deadlock_on_a_handler_that_awaits()
    {
        await using var host = SavePipelineHost.Start(services => services.AddSaveHandler<AwaitsInEveryStage>());
        using var thread = new SingleThread();
        using var scope = host.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Db>();

        var result = thread.Run(() =>
        {
            var before = SynchronizationContext.Current;

            db.Add(new Order { Name = "sync" });
            var saved = db.SaveChanges();
            host.Log.Add("the save returned");

            // The caller gets its synchronization context back.
            return (saved, Restored: ReferenceEquals(before, SynchronizationContext.Current) && before is SingleThread);
        });

        Assert.Equal((1, true), result);
        Assert.Equal(["after save", "before commit", "after commit", "the save returned"], host.Log.Lines);
    }

    [Fact]
    public async Task A_synchronous_commit_runs_before_commit_and_after_commit_and_does_not_deadlock_either()
    {
        await using var host = SavePipelineHost.Start(services => services.AddSaveHandler<AwaitsInEveryStage>());
        using var thread = new SingleThread();
        using var scope = host.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Db>();

        thread.Run(() =>
        {
            using var transaction = db.Database.BeginTransaction();

            db.Add(new Order { Name = "sync" });
            db.SaveChanges();
            host.Log.Add("the save returned");

            transaction.Commit();
            host.Log.Add("the commit returned");
            return 0;
        });

        Assert.Equal(["after save", "the save returned", "before commit", "after commit", "the commit returned"], host.Log.Lines);
    }

    [Fact]
    public async Task Work_that_runs_at_once_is_waited_for_without_a_deadlock_too()
    {
        await using var host = SavePipelineHost.Start();
        using var thread = new SingleThread();
        using var scope = host.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Db>();

        thread.Run(() =>
        {
            db.AfterCommit(async () =>
            {
                await Task.Delay(20);
                host.Log.Add("the work ran");
            });

            host.Log.Add("the call returned");
            return 0;
        });

        Assert.Equal(["the work ran", "the call returned"], host.Log.Lines);
    }

    [Fact]
    public async Task A_synchronous_save_throws_the_handlers_own_exception_and_the_after_commit_exception_as_they_are()
    {
        await using var host = SavePipelineHost.Start(services => services
            .AddScript(new Script<Order>
            {
                AfterSave = async (rows, ct) =>
                {
                    await Task.Delay(1, ct);

                    if (rows[0].Entity.Name == "fails before the commit")
                        throw new ArgumentException("Not this order.");
                },
                AfterCommit = async (rows, ct) =>
                {
                    await Task.Delay(1, ct);

                    if (rows[0].Entity.Name == "fails after the commit")
                        throw new ArgumentException("Could not tell anybody.");
                },
            }));

        using var scope = host.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Db>();

        db.Add(new Order { Name = "fails before the commit" });
        Assert.Equal("Not this order.", Assert.Throws<ArgumentException>(() => db.SaveChanges()).Message);
        db.ChangeTracker.Clear();

        db.Add(new Order { Name = "fails after the commit" });
        var afterCommit = Assert.Throws<AfterCommitException>(() => db.SaveChanges());
        Assert.Equal("Could not tell anybody.", Assert.Single(afterCommit.Failures).Message);

        Assert.Equal(["fails after the commit"], db.Orders.AsNoTracking().Select(x => x.Name).ToList());
    }
}
