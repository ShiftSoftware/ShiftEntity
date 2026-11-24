using Microsoft.EntityFrameworkCore.ChangeTracking;
using ShiftSoftware.ShiftEntity.Core;

namespace ShiftSoftware.ShiftEntity.EFCore;

// The switches of a save. Each is a scope: it applies to the saves that start while it is alive, and to those saves'
// rows through to the commit, even when the scope has ended by then. The scopes that are alive are kept as a stack of
// entries, and each scope takes its own entry out when it ends, so scopes nest and may end in any order: a switch is on
// while one of its scopes is alive, and off once all of them have ended. A pooled context forgets them when it is
// leased again.
public abstract partial class ShiftDbContext
{
    // What a switch scope switches off.
    private enum SwitchKind : byte
    {
        AuditStamping,
        Replication,
        Notifications,
        Attention,
        Handler,
        Stages,
        AllHandlers,
        AfterCommit,
    }

    // One switch scope that is alive. It takes its own entry out of the stack when it is disposed, once.
    private sealed class SwitchScope(ShiftDbContext context, SwitchKind kind, Type? handlerType = null, SaveStages stages = SaveStages.None) : IDisposable
    {
        private bool ended;

        public SwitchKind Kind => kind;
        public Type? HandlerType => handlerType;
        public SaveStages Stages => stages;

        public void Dispose()
        {
            if (ended)
                return;

            ended = true;
            context.EndSwitch(this);
        }
    }

    // The switch scopes that are alive, in the order they began. Null while there are none.
    private List<SwitchScope>? switchScopes;

    private IDisposable BeginSwitch(SwitchKind kind, Type? handlerType = null, SaveStages stages = SaveStages.None)
    {
        ResetIfNewLease();

        var scope = new SwitchScope(this, kind, handlerType, stages);
        (switchScopes ??= []).Add(scope);
        Recompute(kind);

        return scope;
    }

    // A scope that ended takes out its own entry and no other. A scope of a lease that has ended has no entry left in
    // the new lease's stack, so it changes nothing there.
    private void EndSwitch(SwitchScope scope)
    {
        ResetIfNewLease();

        if (switchScopes is null || !switchScopes.Remove(scope))
            return;

        if (switchScopes.Count == 0)
            switchScopes = null;

        Recompute(scope.Kind);
    }

    // Works out what the saves that start now read, from the scopes that are alive.
    private void Recompute(SwitchKind changed)
    {
        switch (changed)
        {
            case SwitchKind.AuditStamping:
                AuditStampingSuppressed = AnySwitch(SwitchKind.AuditStamping);
                return;

            case SwitchKind.Replication:
                ReplicationSuppressed = AnySwitch(SwitchKind.Replication);
                return;

            case SwitchKind.Notifications:
                NotificationsSuppressed = AnySwitch(SwitchKind.Notifications);
                return;

            case SwitchKind.Attention:
                AttentionSuppressed = AnySwitch(SwitchKind.Attention);
                return;
        }

        // The handler switches. A save keeps the set it started with through to its commit, so the set is replaced,
        // never changed in place.
        List<Type>? named = null;
        var off = SaveStages.None;
        bool all = false, afterCommit = false;

        foreach (var scope in switchScopes ?? [])
        {
            switch (scope.Kind)
            {
                case SwitchKind.Handler:
                    if (!(named ??= []).Contains(scope.HandlerType!))
                        named.Add(scope.HandlerType!);
                    break;

                case SwitchKind.Stages:
                    off |= scope.Stages;
                    break;

                case SwitchKind.AllHandlers:
                    all = true;
                    break;

                case SwitchKind.AfterCommit:
                    afterCommit = true;
                    break;
            }
        }

        switches = named is null && off == SaveStages.None && !all && !afterCommit
            ? null
            : new SaveSwitches { Named = named?.ToArray() ?? [], Stages = off, All = all, AfterCommit = afterCommit };
    }

    private bool AnySwitch(SwitchKind kind)
    {
        foreach (var scope in switchScopes ?? [])
            if (scope.Kind == kind)
                return true;

        return false;
    }

    /// <summary>
    /// Backing state for <see cref="SuppressAuditStamping"/>: <see langword="true"/> while at least one suppression
    /// scope is alive.
    /// </summary>
    private bool AuditStampingSuppressed { get; set; }

    /// <summary>
    /// Saves made through the context while the returned scope is alive do not fill the audit columns of the rows they
    /// add. Use it for an infrastructure save that must write exactly the columns it touched and nothing else:
    /// replication bookkeeping, a sync or an import job, a maintenance script. Everything else a save does still
    /// happens. Scopes nest and may end in any order: the switch is off again once every scope has ended.
    /// </summary>
    /// <remarks>
    /// A repository's save is stamped whatever this switch says: its audit columns are part of what a repository save
    /// is.
    /// </remarks>
    public IDisposable SuppressAuditStamping() => BeginSwitch(SwitchKind.AuditStamping);

    /// <summary>
    /// Backing state for <see cref="SuppressReplication"/>: <see langword="true"/> while at least one suppression scope
    /// is alive.
    /// </summary>
    private bool ReplicationSuppressed { get; set; }

    /// <summary>
    /// Saves made while the returned scope is alive are not replicated: no document is written and no watermark
    /// changes. Use it for saves that must not reach another store, such as a development seeder or an import that a
    /// later catch-up replicates in bulk. Scopes nest and may end in any order: the switch is off again once every
    /// scope has ended.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What a later dirty-only catch-up replicates: a row whose save advanced its <c>LastSaveDate</c>, as a repository
    /// save does, stays dirty, and the roots of replication trees are still marked as not replicated when such a save
    /// changes them or a row of their tree (see <c>HasReplicationTree</c>). Any other row the save changed, such as one
    /// edited through a bare context, looks replicated afterwards and needs a forced catch-up.
    /// </para>
    /// <para>
    /// Only replication is skipped. The framework's notification rules and the save handlers still run: each has a
    /// switch of its own, <see cref="SuppressNotifications"/> and <see cref="SuppressSaveHandler{THandler}"/>. A save
    /// that must tell nobody at all uses <see cref="SuppressAfterCommit"/>.
    /// </para>
    /// </remarks>
    public IDisposable SuppressReplication() => BeginSwitch(SwitchKind.Replication);

    /// <summary>
    /// Backing state for <see cref="SuppressNotifications"/>: <see langword="true"/> while at least one suppression scope
    /// is alive.
    /// </summary>
    internal bool NotificationsSuppressed { get; private set; }

    /// <summary>
    /// Saves made while the returned scope is alive raise nothing through the notification rules: <c>RaiseWhenCreated</c>,
    /// an entity's own rules and the rule classes added with <c>AddRule</c>. Everything else a save does still happens:
    /// audit stamping, attention, replication and the save handlers. Use it for a save that must not email anyone,
    /// such as an import of old records. A topic raised in code with <c>INotificationRaiser.RaiseAsync</c> is raised
    /// anyway: that call is the caller's own. Scopes nest and may end in any order: the switch is off again once every
    /// scope has ended.
    /// </summary>
    public IDisposable SuppressNotifications() => BeginSwitch(SwitchKind.Notifications);

    // True while a save must not run the attention evaluators: the save that acknowledges signals.
    private bool AttentionSuppressed { get; set; }

    /// <summary>
    /// Saves made while the returned scope is alive run no attention evaluator. For the save that clears signals:
    /// acknowledging is not a change of the record, and evaluating it would raise a state-based signal again at once.
    /// </summary>
    internal IDisposable SuppressAttention() => BeginSwitch(SwitchKind.Attention);

    // The handlers that are switched off for the saves that start now, worked out from the scopes that are alive. Null
    // while no such switch is on. A save keeps the set it started with.
    private SaveSwitches? switches;

    /// <summary>
    /// Saves made while the returned scope is alive do not run one handler, in any stage it implements. Everything
    /// else still runs: every other handler and every part of the framework. Use it where a save must not start one
    /// particular piece of work, such as a job that updates records and must not push each of them to another system.
    /// It keeps meaning the same when someone adds an unrelated handler later.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rows of such a save are kept from the handler through to the commit, even when the scope has ended by then.
    /// </para>
    /// <para>
    /// This is the only switch that turns a guard off (<see cref="GuardAttribute"/>). In a host that does not have the
    /// handler, the switch does nothing.
    /// </para>
    /// </remarks>
    /// <typeparam name="THandler">The handler class, as it was declared. An open generic handler is named closed.</typeparam>
    public IDisposable SuppressSaveHandler<THandler>() where THandler : class
    {
        var handlerType = typeof(THandler);

        if (SaveHandlerRegistration.StagesOf(handlerType).Count == 0)
            throw new ArgumentException(
                $"{handlerType.Name} is not a save handler, so there is nothing to switch off: it implements none of IBeforeSaveHandler<T>, " +
                "IAfterSaveHandler<T>, IBeforeCommitHandler<T> and IAfterCommitHandler<T>. Name the handler class itself.", nameof(THandler));

        return BeginSwitch(SwitchKind.Handler, handlerType);
    }

    /// <summary>
    /// Saves made while the returned scope is alive run no handler in the named stages, except the guards
    /// (<see cref="GuardAttribute"/>). The other stages and every part of the framework still run. Use it for a save
    /// that must skip a whole stage, such as an import that must start no message, push or email:
    /// <c>SuppressSaveHandlers(SaveStages.AfterCommit)</c>.
    /// </summary>
    /// <remarks>
    /// The rows of such a save are kept from those handlers through to the commit, even when the scope has ended by
    /// then. Naming no stage throws: a switch that names nothing is a mistake.
    /// </remarks>
    /// <param name="stages">The stages, combined with <c>|</c>.</param>
    public IDisposable SuppressSaveHandlers(SaveStages stages)
    {
        const SaveStages every = SaveStages.BeforeSave | SaveStages.AfterSave | SaveStages.BeforeCommit | SaveStages.AfterCommit;

        if (stages == SaveStages.None || (stages & ~every) != 0)
            throw new ArgumentException(
                "SuppressSaveHandlers needs the stages to switch off: SaveStages.BeforeSave, AfterSave, BeforeCommit or AfterCommit, " +
                "or several of them. To switch one handler off, use SuppressSaveHandler<THandler>(); to switch every handler off, " +
                "SuppressAllSaveHandlers().", nameof(stages));

        return BeginSwitch(SwitchKind.Stages, stages: stages);
    }

    /// <summary>
    /// Saves made while the returned scope is alive run no handler in any stage, except the guards
    /// (<see cref="GuardAttribute"/>). That includes the handlers that keep data right, and the ones a package
    /// declares. The framework's parts still run: audit stamping, attention, the notification rules and replication.
    /// </summary>
    /// <remarks>
    /// The rows of such a save are kept from the handlers through to the commit, even when the scope has ended by then.
    /// Most saves that must skip something mean one handler (<see cref="SuppressSaveHandler{THandler}"/>) or the stage
    /// that tells other systems (<see cref="SuppressAfterCommit"/>).
    /// </remarks>
    public IDisposable SuppressAllSaveHandlers() => BeginSwitch(SwitchKind.AllHandlers);

    /// <summary>
    /// Saves made while the returned scope is alive tell nobody: nothing the framework would do after the commit
    /// happens for them. No row is replicated, no notification rule raises an event, no attention event is published,
    /// and no after-commit handler runs. Use it for a save that no other system may hear of, such as a development
    /// seeder.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything that keeps the data right still runs: audit stamping, the attention evaluators, the before-save,
    /// after-save and before-commit handlers, and the marking of replication-tree roots, so that a later catch-up
    /// replicates what was skipped.
    /// </para>
    /// <para>
    /// Work the code queues itself with <see cref="AfterCommit(Action)"/> is the caller's own and still runs.
    /// </para>
    /// </remarks>
    public IDisposable SuppressAfterCommit() => BeginSwitch(SwitchKind.AfterCommit);

    // How many repository saves are running on this context, for hosts that evaluate attention only on those
    // (AttentionSaves.Repository). A save made from inside a repository save counts as one there.
    private int repositorySaves;

    // The repository save that is about to start: the next save of the context is it.
    private RepositorySave? startingRepositorySave;

    /// <summary>
    /// Tells the context that the save a repository is about to make is a repository's: its modified rows get their
    /// last-save columns too, a before-save handler sees <see cref="PendingRows{TEntity}.IsRepositorySave"/>, and the
    /// repository's own steps run at their places in the save. A save made from inside that save is not one.
    /// </summary>
    internal IDisposable StartRepositorySave(RepositorySave save)
    {
        ResetIfNewLease();
        var previous = startingRepositorySave;
        startingRepositorySave = save;
        repositorySaves++;

        return new RestoreScope(() =>
        {
            repositorySaves--;
            startingRepositorySave = previous;
        });
    }

    private RepositorySave? TakeRepositorySave()
    {
        var save = startingRepositorySave;
        startingRepositorySave = null;
        return save;
    }

    private sealed class RestoreScope(Action restore) : IDisposable
    {
        private Action? restore = restore;

        public void Dispose()
        {
            restore?.Invoke();
            restore = null;
        }
    }
}

/// <summary>What a repository tells the context about the save it is about to make.</summary>
internal sealed class RepositorySave
{
    /// <summary>The repository's own entity type. Its steps below are for the rows of that type.</summary>
    public required Type EntityType { get; init; }

    /// <summary>Who saves, for the audit columns.</summary>
    public long? UserId { get; init; }

    /// <summary>The claims the creation columns of new rows are filled from.</summary>
    public IdentityClaimProvider? Claims { get; init; }

    /// <summary>
    /// Runs once before the write, after every before-save handler, with the added and modified rows of the
    /// repository's type as they are about to be written. It may change them through their entries only.
    /// </summary>
    /// <returns>
    /// What the repository does after the write and after the after-save handlers, or null for nothing. It runs inside
    /// the save's transaction, or after the write of a save that needed none, where its failure is reported and undoes
    /// nothing.
    /// </returns>
    public Func<IReadOnlyList<EntityEntry>, Func<Task>?>? BeforeWrite { get; init; }
}
