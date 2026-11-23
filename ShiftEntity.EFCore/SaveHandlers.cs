using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;

namespace ShiftSoftware.ShiftEntity.EFCore;

/// <summary>
/// Host code that runs after a save has written a row of <typeparamref name="TEntity"/>: send a message, call another
/// system, write a follow-up record. Register it with <c>services.AddSaveHandler&lt;TEntity, THandler&gt;()</c>. It takes
/// the place of an after-save trigger of a trigger library.
/// </summary>
/// <remarks>
/// <para>
/// It runs for every save of a <see cref="ShiftDbContext"/> that writes such a row, through a repository or a plain
/// <c>SaveChanges</c>, for added, modified and deleted rows alike: check <see cref="SavedRow{TEntity}.Change"/>.
/// <typeparamref name="TEntity"/> can also be a base class or an interface, and then the handler runs for every row
/// that is one.
/// </para>
/// <para>
/// <b>When.</b> Inside the save call, once EF has written and accepted every row of the save, after the framework's own
/// steps (replication, notification rules) and before the call returns. The handlers of one save run one after another,
/// row by row in the order the rows were saved, and for one row in the order the handlers were registered. When a
/// transaction is open, the handler runs inside it, before the commit. For work that must wait for the commit, such as a
/// message to another system, use <see cref="ShiftDbContext.AfterCommit"/> on <see cref="SavedRow{TEntity}.Context"/>.
/// </para>
/// <para>
/// <b>Saving again.</b> A handler may change records and save through the same context. That save is a save like any
/// other: it runs the handlers of the rows it writes, this one included. A handler that saves the row it was called for
/// either checks that there is still something to do, or saves with
/// <see cref="ShiftDbContext.SaveChangesWithoutHandlersAsync"/>.
/// </para>
/// <para>
/// <b>Failures.</b> An exception leaves the save call, and the handlers still waiting don't run. The rows are already
/// written: without a transaction they stay saved, and inside one the caller's rollback takes them back. Catch what must
/// not fail the caller.
/// </para>
/// <para>
/// <b>Not run</b> for saves made with <see cref="ShiftDbContext.SaveChangesWithoutHandlersAsync"/> or inside
/// <see cref="ShiftDbContext.SuppressSaveHandlers"/>, for saves that don't accept their changes
/// (<c>acceptAllChangesOnSuccess: false</c>), and for rows written without the change tracker
/// (<c>ExecuteUpdate</c>, <c>ExecuteDelete</c>, raw SQL). A save under <see cref="ShiftDbContext.SuppressReplication"/>
/// still runs its handlers. A trigger library's own switch, such as its <c>SaveChangesWithoutTriggers</c>, knows nothing
/// about handlers: a host that moves a trigger here moves the saves that skipped it too.
/// </para>
/// <para>
/// The handler is resolved from the scope the context came from, only when a row of its type was saved. It is registered
/// as a scoped service unless the host registered the class itself. A context from a pool or a factory has no scope, so
/// its handlers come from the root provider and can't use scoped services.
/// </para>
/// </remarks>
/// <typeparam name="TEntity">The row's entity type, or a base class or interface its type has.</typeparam>
public interface ISaveHandler<TEntity> where TEntity : class
{
    /// <summary>Called once for each row of the type that a save wrote.</summary>
    ValueTask HandleAsync(SavedRow<TEntity> row, CancellationToken cancellationToken);
}

/// <summary>One row that a save wrote, as a save handler sees it.</summary>
/// <typeparam name="TEntity">The type the handler is registered for.</typeparam>
public sealed class SavedRow<TEntity> where TEntity : class
{
    private readonly TEntity? original;

    /// <summary>
    /// The saved record: the tracked instance, holding what the save wrote, generated keys included. After a delete, the
    /// instance the context no longer tracks.
    /// </summary>
    public required TEntity Entity { get; init; }

    /// <summary>Whether the save added, modified or deleted the row.</summary>
    public required SavedRowChange Change { get; init; }

    /// <summary>The context that saved the row. Use it to save again, or to run something after the commit.</summary>
    public required ShiftDbContext Context { get; init; }

    /// <summary>
    /// The row's values from before the save, as a detached copy. Null for an added or deleted row. Only for a handler
    /// registered with <c>withOriginal: true</c>: the copy costs memory for every modified row, so a handler that didn't
    /// ask for it gets an exception here instead of a null that reads as "nothing changed".
    /// </summary>
    /// <remarks>
    /// The copy is EF's own picture of the row before the save, so it holds the mapped columns only: no navigations, no
    /// owned types and no unmapped members. It is what the database held only when this context loaded the row. For a
    /// row attached as modified (<c>Update</c>), it is what the caller passed in.
    /// </remarks>
    public TEntity? Original
    {
        get => HasOriginal
            ? original
            : throw new InvalidOperationException(
                $"This save handler of {typeof(TEntity).Name} was registered without the values from before the save. " +
                $"Register it with AddSaveHandler<{typeof(TEntity).Name}, THandler>(withOriginal: true).");
        init => original = value;
    }

    /// <summary>Whether the handler asked for <see cref="Original"/>. True for a row a test builds itself.</summary>
    internal bool HasOriginal { get; init; } = true;
}

/// <summary>One <c>AddSaveHandler</c> call: the type the handler is for, whether it reads old values, and how to call it.</summary>
internal sealed record SaveHandlerRegistration(Type EntityType, bool WithOriginal, SaveHandlerInvoker Invoker);

/// <summary>Calls one handler with the row typed the way the handler is written.</summary>
internal abstract class SaveHandlerInvoker
{
    public abstract ValueTask InvokeAsync(IServiceProvider services, ShiftDbContext context, SavedRow row, CancellationToken cancellationToken);
}

internal sealed class SaveHandlerInvoker<TEntity>(Type handlerType, bool withOriginal) : SaveHandlerInvoker where TEntity : class
{
    public Type HandlerType => handlerType;

    public override ValueTask InvokeAsync(IServiceProvider services, ShiftDbContext context, SavedRow row, CancellationToken cancellationToken)
    {
        var handler = (ISaveHandler<TEntity>)services.GetRequiredService(handlerType);

        return handler.HandleAsync(new SavedRow<TEntity>
        {
            Entity = (TEntity)row.Entity,
            Change = row.Change,
            Context = context,
            Original = withOriginal ? (TEntity?)row.Original : null,
            HasOriginal = withOriginal,
        }, cancellationToken);
    }
}

/// <summary>The host's save handlers, and which of them apply to each entity type. One per host.</summary>
internal sealed class SaveHandlerRegistry(IEnumerable<SaveHandlerRegistration> registrations)
{
    private readonly SaveHandlerRegistration[] registrations = registrations.ToArray();
    private readonly ConcurrentDictionary<Type, SaveHandlerRegistration[]> byType = new();

    /// <summary>The handlers of rows of this type, in the order they were registered.</summary>
    public SaveHandlerRegistration[] For(Type entityType) => byType.GetOrAdd(entityType,
        type => registrations.Where(registration => registration.EntityType.IsAssignableFrom(type)).ToArray());
}

/// <summary>
/// Runs the host's save handlers for the rows a save wrote. It hears about a save after every other observer, because a
/// handler may change the saved records and save again.
/// </summary>
internal sealed class SaveHandlersObserver(IServiceProvider services) : IShiftDbContextSaveObserver, ISavedRowOriginals, IOrderedSaveObserver, ISwitchedSaveObserver
{
    private SaveHandlerRegistry? registry;

    private SaveHandlerRegistry Registry => registry ??= services.GetRequiredService<SaveHandlerRegistry>();

    public SaveObserverOrder Order => SaveObserverOrder.Last;

    // SaveChangesWithoutHandlersAsync and SuppressSaveHandlers. SuppressReplication doesn't silence the handlers.
    public bool IsSuppressed(ShiftDbContext context) => context.SaveHandlersSuppressed;

    public bool NeedsOriginalOf(Type entityType)
    {
        foreach (var registration in Registry.For(entityType))
            if (registration.WithOriginal)
                return true;

        return false;
    }

    public async ValueTask SavedAsync(ShiftDbContext context, SaveReport report, CancellationToken cancellationToken)
    {
        // The report is this save's own list. A handler that saves again gets a report of its own for that save.
        Type? lastType = null;
        SaveHandlerRegistration[] handlers = [];

        foreach (var row in report.Rows)
        {
            if (row.EntityType != lastType)
            {
                lastType = row.EntityType;
                handlers = Registry.For(row.EntityType);
            }

            foreach (var handler in handlers)
                await handler.Invoker.InvokeAsync(services, context, row, cancellationToken);
        }
    }
}
