using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ShiftSoftware.ShiftEntity.EFCore.Replication;
using Microsoft.Extensions.Options;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Attention;
using ShiftSoftware.ShiftEntity.Core.Tagging;
using ShiftSoftware.ShiftEntity.EFCore.Attention;
using ShiftSoftware.ShiftEntity.EFCore.Tagging;
using System.Collections.Concurrent;
using System.Reflection;
using System.Transactions;

namespace ShiftSoftware.ShiftEntity.EFCore;

public abstract class ShiftDbContext : DbContext
{
    internal ShiftDbContextExtensionOptions? ShiftDbContextOptions { get; set; }

    /// <summary>
    /// Tag vocabulary for this DbContext. Ignored from the EF model — and therefore
    /// from migrations — unless the application calls <c>services.AddShiftTagging&lt;TDbContext&gt;()</c>.
    /// Access this set without registering tagging will throw at runtime.
    /// </summary>
    public DbSet<Tag> Tags { get; set; } = default!;

    public ShiftDbContext() : base()
    {
    }

    private readonly IServiceProvider? _applicationServiceProvider;

    internal IServiceProvider? ApplicationServiceProvider => _applicationServiceProvider;

    public ShiftDbContext(DbContextOptions options) : base(options)
    {
        ShiftDbContextOptions = options.Extensions
            .OfType<ShiftDbContextExtensionOptions>()
            .FirstOrDefault();

        _applicationServiceProvider = options.Extensions
            .OfType<CoreOptionsExtension>()
            .FirstOrDefault()
            ?.ApplicationServiceProvider;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Invoke contributors before ConfigureShiftEntity so that ConfigureShiftEntity
        // can apply conventions (e.g. DeleteBehavior.Restrict) to all relationships,
        // including those added by contributors.
        var contributors = _applicationServiceProvider?.GetService<IEnumerable<IModelBuildingContributor>>();
        if (contributors is not null)
        {
            foreach (var contributor in contributors)
            {
                contributor.Configure(modelBuilder);
            }
        }

        var taggingOptions = _applicationServiceProvider?.GetService<IOptions<ShiftTaggingOptions>>()?.Value;
        var effectiveTaggingOptions = taggingOptions?.Enabled == true ? taggingOptions : null;

        modelBuilder.ConfigureShiftEntity(ShiftDbContextOptions?.UseTemporal ?? false, effectiveTaggingOptions);
    }

    /// <summary>
    /// On SQLite, keeps a shared in-memory database alive between contexts and warns once about one that is not shared
    /// (see <see cref="Sqlite.SqliteInMemory"/>). A context that overrides this and configures its provider here must
    /// call the base method after configuring it.
    /// </summary>
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);

        Sqlite.SqliteInMemory.Apply(optionsBuilder.Options, _applicationServiceProvider
            ?? optionsBuilder.Options.FindExtension<CoreOptionsExtension>()?.ApplicationServiceProvider);
    }

    /// <summary>
    /// Adds the convention that collects this context's replication trees (<c>HasReplicationTree</c>) when EF finalizes
    /// the model. A context that overrides this must call the base method.
    /// </summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
        var contextType = GetType();
        configurationBuilder.Conventions.Add(_ => new ReplicationTrees.Convention(contextType));
    }

    /// <summary>
    /// Backing state for <see cref="SuppressAuditStamping"/>: <see langword="true"/> while at least one suppression
    /// scope is alive, making the SaveChanges override skip its insert backfill. Only the scope touches this — every
    /// caller, the repository included, goes through <see cref="SuppressAuditStamping"/>.
    /// </summary>
    private bool AuditStampingSuppressed { get; set; }

    /// <summary>
    /// Suppresses every audit backfill this context's SaveChanges override would perform until the returned scope is
    /// disposed. Two kinds of callers use it: <see cref="ShiftRepository{DB,EntityType,ListDTO,ViewAndUpsertDTO}"/>
    /// wraps its own save in it because its sweep has already stamped the tracked entries, and infrastructure saves
    /// (replication bookkeeping, sync/import jobs, maintenance scripts) use it to write exactly the columns they
    /// touched and nothing else. Composes with any save method, including <c>SaveChangesWithoutTriggersAsync</c>:
    /// <code>
    /// using (db.SuppressAuditStamping())
    ///     await db.SaveChangesWithoutTriggersAsync();
    /// </code>
    /// Disposal restores the previous suppression state, so scopes nest safely.
    /// </summary>
    public IDisposable SuppressAuditStamping() => new AuditStampingSuppressionScope(this);

    private sealed class AuditStampingSuppressionScope : IDisposable
    {
        private readonly ShiftDbContext context;
        private readonly bool previous;

        public AuditStampingSuppressionScope(ShiftDbContext context)
        {
            this.context = context;
            context.ResetIfNewLease();
            previous = context.AuditStampingSuppressed;
            context.AuditStampingSuppressed = true;
        }

        public void Dispose() => context.AuditStampingSuppressed = previous;
    }

    /// <summary>
    /// Backing state for <see cref="SuppressReplication"/>: <see langword="true"/> while at least one suppression scope
    /// is alive.
    /// </summary>
    private bool ReplicationSuppressed { get; set; }

    /// <summary>
    /// Saves made while the returned scope is alive report no rows to the registered
    /// <see cref="IShiftDbContextSaveObserver"/>s, such as replication's, so nothing is replicated for them: no document
    /// is written and no watermark changes. Use it for saves that must not reach another store, such as a development
    /// seeder or an import that a later catch-up replicates in bulk. Disposal restores the previous state, so scopes nest
    /// safely.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What a later dirty-only catch-up replicates: a row whose save advanced its <c>LastSaveDate</c>, as a repository
    /// save does, stays dirty, and the roots of replication trees are still marked as not replicated when such a save
    /// changes them or a row of their tree (see <c>HasReplicationTree</c>). Any other row the save changed, such as one
    /// edited through a bare context, looks replicated afterwards and needs a forced catch-up.
    /// </para>
    /// <para>
    /// Only replication is skipped. The framework's notification rules and the host's save handlers
    /// (<see cref="ISaveHandler{TEntity}"/>) still run: each has a switch of its own, <see cref="SuppressNotifications"/>
    /// and <see cref="SuppressSaveHandlers"/>. A save that must reach nothing at all uses the three together.
    /// </para>
    /// </remarks>
    public IDisposable SuppressReplication() => new ReplicationSuppressionScope(this);

    private sealed class ReplicationSuppressionScope : IDisposable
    {
        private readonly ShiftDbContext context;
        private readonly bool previous;

        public ReplicationSuppressionScope(ShiftDbContext context)
        {
            this.context = context;
            context.ResetIfNewLease();
            previous = context.ReplicationSuppressed;
            context.ReplicationSuppressed = true;
        }

        public void Dispose() => context.ReplicationSuppressed = previous;
    }

    /// <summary>
    /// Backing state for <see cref="SuppressSaveHandlers"/>: <see langword="true"/> while at least one suppression scope
    /// is alive.
    /// </summary>
    internal bool SaveHandlersSuppressed { get; private set; }

    /// <summary>
    /// Saves made while the returned scope is alive run none of the host's save handlers
    /// (<see cref="ISaveHandler{TEntity}"/>). Everything else a save does still happens: audit stamping, attention,
    /// replication and notification rules. Use it for a save whose handlers must not run, such as an import of old
    /// records, or the save a handler makes to store its own outcome. Disposal restores the previous state, so scopes
    /// nest safely. For a single save, <see cref="SaveChangesWithoutHandlersAsync"/> reads better.
    /// </summary>
    public IDisposable SuppressSaveHandlers()
    {
        ResetIfNewLease();
        var previous = SaveHandlersSuppressed;
        SaveHandlersSuppressed = true;
        return new RestoreScope(() => SaveHandlersSuppressed = previous);
    }

    /// <summary>
    /// Saves without running the host's save handlers (<see cref="ISaveHandler{TEntity}"/>). The same as
    /// <c>SaveChangesAsync</c> inside <see cref="SuppressSaveHandlers"/>.
    /// </summary>
    public async Task<int> SaveChangesWithoutHandlersAsync(CancellationToken cancellationToken = default)
    {
        using (SuppressSaveHandlers())
            return await SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc cref="SaveChangesWithoutHandlersAsync"/>
    public int SaveChangesWithoutHandlers()
    {
        using (SuppressSaveHandlers())
            return SaveChanges();
    }

    /// <summary>
    /// Backing state for <see cref="SuppressNotifications"/>: <see langword="true"/> while at least one suppression scope
    /// is alive.
    /// </summary>
    internal bool NotificationsSuppressed { get; private set; }

    /// <summary>
    /// Saves made while the returned scope is alive raise nothing through the notification rules: <c>RaiseWhenCreated</c>,
    /// an entity's own rules and the rule classes added with <c>AddRule</c>. Everything else a save does still happens:
    /// audit stamping, attention, replication and the host's save handlers. Use it for a save that must not email anyone,
    /// such as an import of old records. A topic raised in code with <c>INotificationRaiser.RaiseAsync</c> is raised
    /// anyway: that call is the caller's own. Disposal restores the previous state, so scopes nest safely.
    /// </summary>
    public IDisposable SuppressNotifications()
    {
        ResetIfNewLease();
        var previous = NotificationsSuppressed;
        NotificationsSuppressed = true;
        return new RestoreScope(() => NotificationsSuppressed = previous);
    }

    // True while a save must not run the attention evaluators: the save that acknowledges signals.
    private bool AttentionSuppressed { get; set; }

    /// <summary>
    /// Saves made while the returned scope is alive run no attention evaluator. For the save that clears signals:
    /// acknowledging is not a change of the record, and evaluating it would raise a state-based signal again at once.
    /// </summary>
    internal IDisposable SuppressAttention()
    {
        ResetIfNewLease();
        var previous = AttentionSuppressed;
        AttentionSuppressed = true;
        return new RestoreScope(() => AttentionSuppressed = previous);
    }

    // How many repository saves are running on this context, for hosts that evaluate attention only on those
    // (AttentionSaves.Repository).
    private int repositorySaves;

    /// <summary>Marks the saves made while the returned scope is alive as a repository's.</summary>
    internal IDisposable RepositorySave()
    {
        ResetIfNewLease();
        repositorySaves++;
        return new RestoreScope(() => repositorySaves--);
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

    // The services of the scope this context came from, or EF's own when it was built without one.
    private IServiceProvider ServicesOfSave => _applicationServiceProvider ?? ((IInfrastructure<IServiceProvider>)this).Instance;

    // The observers, resolved once per context from its application service provider, in the order they hear about a
    // save: the framework's observers that read what the save wrote, then the others as registered, then the one that
    // runs the host's save handlers, which may change the records and save again. Empty without DI.
    private IShiftDbContextSaveObserver[]? saveObservers;

    private IShiftDbContextSaveObserver[] SaveObservers =>
        saveObservers ??= _applicationServiceProvider?.GetServices<IShiftDbContextSaveObserver>()
            .OrderBy(observer => observer is IOrderedSaveObserver ordered ? (int)ordered.Order : (int)SaveObserverOrder.Registered)
            .ToArray() ?? [];

    // The observers that hear about a save starting now, in their order. SuppressReplication silences the plain
    // observers, such as replication's. A framework observer with a switch of its own (the notification rules, the
    // host's save handlers) is silenced by that switch alone. Without any switch on, this is the list itself.
    private IShiftDbContextSaveObserver[] ListenersOfThisSave()
    {
        var observers = SaveObservers;
        var silenced = 0;

        foreach (var observer in observers)
            if (IsSilenced(observer))
                silenced++;

        if (silenced == 0)
            return observers;

        return silenced == observers.Length ? [] : observers.Where(observer => !IsSilenced(observer)).ToArray();
    }

    private bool IsSilenced(IShiftDbContextSaveObserver observer) =>
        observer is ISwitchedSaveObserver switched ? switched.IsSuppressed(this) : ReplicationSuppressed;

    // The rows the save in progress has written. A save made from inside another save (a save handler, or a trigger
    // library's after-save step) collects its own rows and puts the outer save's list back when it ends. The change
    // tracker is watched only while a save that collects runs: a pooled context loses its handlers when it goes back to
    // the pool, and a row whose state changes outside a save was not saved.
    private List<SavedRow>? savedRows;
    private int collectingSaves;

    // What the modified rows of the save in progress held before it, by tracked instance, for the observers that asked
    // (ISavedRowOriginals). A nested save keeps its own and puts the outer save's back, as with savedRows. Which entity
    // types need them is worked out once per type for this context.
    private Dictionary<object, object>? savedOriginals;
    private ISavedRowOriginals[]? originalsObservers;
    private Dictionary<Type, bool>? needsOriginal;
    private Type[]? typesNeedingOriginals;

    // Whether the save in progress is inside EF's own save, and whether its rows have been copied because a save from
    // in there was about to write them again (see KeepWhatWasWritten).
    private bool writing;
    private bool writtenKept;

    private ISavedRowOriginals[] OriginalsObservers =>
        originalsObservers ??= SaveObservers.OfType<ISavedRowOriginals>().ToArray();

    // Whether the model has a type that takes part in attention. Worked out once per model, read once per context.
    private bool? modelHasAttention;

    private bool ModelHasAttention => modelHasAttention ??= AttentionSave.AppliesTo(Model);

    // The work the saves of an open transaction asked to run after its commit, and that transaction: the last one this
    // context began (the interceptor tells it) or opened itself for a save. Work left by a transaction that ended
    // without a commit or a rollback, such as one disposed after an exception, is dropped when the next one begins.
    private List<Action>? afterCommit;
    private Guid? afterCommitTransaction;
    private bool? afterCommitInterceptorAdded;

    // The work queued in an ambient or enlisted transaction, which one handler of that transaction's completion runs.
    // The context follows one such transaction at a time; the next one replaces it.
    private AmbientWork? ambientAfterCommit;

    private sealed class AmbientWork(string transaction, Action first)
    {
        public string Transaction { get; } = transaction;
        public List<Action> Actions { get; } = [first];
    }

    // The lease of a pooled context that the state above belongs to.
    private int lease;

    // A pooled context goes back to the pool and is leased again as the same instance. EF resets its own state then;
    // this context's state of an earlier lease, such as work queued for a transaction that never committed or a
    // suppression scope never disposed, is dropped when the new lease first uses the context.
    private void ResetIfNewLease()
    {
        var current = ContextId.Lease;
        if (current == lease)
            return;

        lease = current;
        afterCommit = null;
        afterCommitTransaction = null;
        AuditStampingSuppressed = false;
        ReplicationSuppressed = false;
        SaveHandlersSuppressed = false;
        NotificationsSuppressed = false;
        AttentionSuppressed = false;
        repositorySaves = 0;
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess) =>
        SaveCoreAsync(acceptAllChangesOnSuccess, async: false, default).GetAwaiter().GetResult();

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default) =>
        SaveCoreAsync(acceptAllChangesOnSuccess, async: true, cancellationToken);

    // The save both overrides share. With async false the context's own calls are the synchronous ones. What an
    // observer does asynchronously is waited for, as the trigger library did for its after-save step.
    //
    // A save has four parts. Before the write: audit stamping, attention, and what replication trees need. The write:
    // EF's own save, during which the context collects the rows EF accepts. After the write, in the save's transaction:
    // the signals that waited for a new row's key, the roots of replication trees, and the observers (notification
    // rules, replication, then the host's save handlers). After the commit: whatever those queued with AfterCommit.
    private async Task<int> SaveCoreAsync(bool acceptAllChangesOnSuccess, bool async, CancellationToken cancellationToken)
    {
        ResetIfNewLease();

        var outer = savedRows;
        var outerOriginals = savedOriginals;
        var outerWriting = writing;
        var outerKept = writtenKept;
        var collecting = false;
        IDbContextTransaction? own = null;
        var savepoints = false;

        try
        {
            // This save runs inside another save's write: an after-save step of EF's own pipeline, such as a trigger
            // library's, is saving again. It may be about to write rows the outer save wrote, before the outer save's
            // observers have read them.
            if (outer is not null && outerWriting && !outerKept)
            {
                KeepWhatWasWritten(outer);
                outerKept = true;
            }

            var (attention, detected) = await PrepareBeforeWriteAsync(acceptAllChangesOnSuccess, async, cancellationToken);

            // The roots of replication trees are marked whenever the context declares trees, also when nothing is
            // replicated now (under SuppressReplication, without an observer, or when the changes are not accepted),
            // so that a later catch-up rebuilds them. The pass before the write has just detected the changes, unless
            // nothing asked for it.
            var trees = ReplicationTreeSave.Prepare(this, detectChanges: !detected);

            if (trees is { NeedsTransaction: true })
                RefuseRetryingStrategy();

            // Marking a tree's roots must commit or roll back with the save, and so must the signals of new rows of an
            // indexed attention type, which are written second. Such a save runs in a transaction: the caller's, an
            // ambient or enlisted one, or else one the context opens itself. A provider without transactions, such as
            // the in-memory one, has none to open for the signals.
            //
            // LOUD: EF accepts the save's changes inside base.SaveChanges, before the roots are marked and before this
            // transaction commits. When the mark, an observer (a preparer), the caller's cancellation or the commit
            // fails after that, SQL rolls back but the context's rows say Unchanged, so a retry on the same context
            // saves nothing. That is why a retrying execution strategy is refused above for trees. After such a failure
            // the caller must start again on a new context.
            var needsTransaction = trees is { NeedsTransaction: true } || (attention is { HasPendingRows: true } && Database.IsRelational());

            if (needsTransaction && CanOpenOwnTransaction())
            {
                own = async ? await Database.BeginTransactionAsync(cancellationToken) : Database.BeginTransaction();
                OnTransactionStarted(own.TransactionId);

                // EF would set a savepoint before writing, which nothing here rolls back to: a failure rolls the
                // whole transaction back.
                savepoints = Database.AutoSavepointsEnabled;
                Database.AutoSavepointsEnabled = false;
            }

            // The observers this save is reported to, decided as it starts: the switches on now apply to it.
            var listeners = ListenersOfThisSave();
            var rows = StartCollecting(acceptAllChangesOnSuccess, listeners);
            collecting = rows is not null;
            savedOriginals = collecting ? CaptureOriginals() : null;
            writtenKept = false;

            int result;
            try
            {
                writing = true;

                result = async
                    ? await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken)
                    : base.SaveChanges(acceptAllChangesOnSuccess);

                // The new rows have their keys now, so the signals that waited for them are written, in the same
                // transaction. The count the caller gets stays the count of its own rows.
                if (attention is { HasPendingRows: true })
                {
                    attention.AddPendingRows(this);

                    if (async)
                        await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
                    else
                        base.SaveChanges(acceptAllChangesOnSuccess);
                }
            }
            finally
            {
                writing = outerWriting;

                if (collecting)
                {
                    collecting = false;
                    StopCollecting();
                }
                savedRows = outer;
                savedOriginals = outerOriginals;
                writtenKept = outerKept;
            }

            if (attention is not null)
                PublishAttentionAfterCommit(attention);

            var changedRoots = trees is null ? NoRoots : await trees.FindAndMarkRootsAsync(this, async, cancellationToken);

            if (rows is not null && (rows.Count > 0 || changedRoots.Count > 0))
                await ReportSavedAsync(new SaveReport(rows, changedRoots), listeners, cancellationToken);

            if (own is not null)
            {
                if (async)
                    await own.CommitAsync(cancellationToken);
                else
                    own.Commit();

                // The interceptor has run the work already when it is there; without it, the work runs now.
                OnTransactionCommitted(own.TransactionId);
            }

            return result;
        }
        catch
        {
            if (own is not null)
            {
                OnTransactionEnded();
                try
                {
                    if (async)
                        await own.RollbackAsync(CancellationToken.None);
                    else
                        own.Rollback();
                }
                catch
                {
                    // The original failure is the one to report; disposing the transaction ends it anyway.
                }
            }
            throw;
        }
        finally
        {
            // Also when the save failed before it wrote anything, for example when its transaction could not begin.
            if (collecting)
                StopCollecting();
            savedRows = outer;
            savedOriginals = outerOriginals;
            writing = outerWriting;
            writtenKept = outerKept;

            if (own is not null)
            {
                Database.AutoSavepointsEnabled = savepoints;
                if (async)
                    await own.DisposeAsync();
                else
                    own.Dispose();
            }
        }
    }

    private static readonly IReadOnlyDictionary<Type, IReadOnlyCollection<long>> NoRoots = new Dictionary<Type, IReadOnlyCollection<long>>();

    // A save that changes a replication tree marks its roots after EF has accepted its changes (see SaveCoreAsync), so
    // a retried attempt would find nothing left to save and lose the changes without an error. Such a save is refused
    // at once, before it writes anything, under an execution strategy that retries: the context's own, or one that runs
    // the save as part of a larger operation. While a strategy runs one, every strategy says that it does not retry, so
    // the running one is asked too.
    private void RefuseRetryingStrategy()
    {
        IExecutionStrategy? retrying = ExecutionStrategy.Current is { MaxRetryCount: > 0 } executing
            ? executing
            : Database.CreateExecutionStrategy() is { RetriesOnFailure: true } strategy ? strategy : null;

        if (retrying is not null)
            throw new InvalidOperationException(
                $"{GetType().Name} cannot save rows of a replication tree under an execution strategy that retries on failure " +
                $"({retrying.GetType().Name}). Such a save marks the tree's roots after EF has accepted its changes, so a retried " +
                "attempt would find nothing to save and lose the changes without an error. Configure this context without a " +
                "retrying execution strategy, for example without EnableRetryOnFailure.");
    }

    // An ambient (TransactionScope) or enlisted transaction already makes the mark commit or roll back with the save,
    // and EF refuses a transaction of the context's own inside one.
    private bool CanOpenOwnTransaction() =>
        Database.CurrentTransaction is null && AmbientTransaction() is null;

    private System.Transactions.Transaction? AmbientTransaction() =>
        System.Transactions.Transaction.Current ?? (Database.IsRelational() ? Database.GetEnlistedTransaction() : null);

    /// <summary>
    /// Runs <paramref name="action"/> once the rows saved so far are committed: at once when no transaction is open,
    /// and when the open one commits otherwise. When that transaction rolls back, fails or ends without a commit, the
    /// action is dropped. The action runs while EF still holds the committed transaction, so it must not use this
    /// context.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Holding the action until the commit of a caller's explicit transaction, or of an ambient
    /// (<c>TransactionScope</c>) or enlisted one, needs <c>UseShiftEntityAfterCommit()</c> on the context's options.
    /// Without it, the action runs at once even inside that transaction, as replication did before it waited for the
    /// commit. A transaction the context opens itself for a save is always followed. A transaction the context joined
    /// with <c>UseTransaction</c> is committed by its owner, which this context is not told about, so there the action
    /// runs at once too. The actions queued in one transaction run in the order they were queued.
    /// </para>
    /// <para>
    /// Replication through a pooled context (<c>AddDbContextPool</c>, <c>AddPooledDbContextFactory</c>) is not
    /// supported: the pool builds the context's options once, so a save resolves the services its replication needs
    /// from the root provider, and the registrations and preparers are scoped services.
    /// </para>
    /// </remarks>
    public void AfterCommit(Action action)
    {
        ResetIfNewLease();

        var current = Database.CurrentTransaction;
        if (current is not null)
        {
            if (current.TransactionId == afterCommitTransaction)
                (afterCommit ??= new()).Add(action);
            else
                action();
            return;
        }

        if (AfterCommitInterceptorAdded && AmbientTransaction() is { } ambient)
        {
            // One handler for each transaction, which runs the work of all its saves in order.
            var id = ambient.TransactionInformation.LocalIdentifier;
            if (ambientAfterCommit is { } queued && queued.Transaction == id)
            {
                queued.Actions.Add(action);
                return;
            }

            var work = ambientAfterCommit = new AmbientWork(id, action);
            ambient.TransactionCompleted += (_, e) =>
            {
                // It can complete on another thread; a later transaction's work is left alone.
                Interlocked.CompareExchange(ref ambientAfterCommit, null, work);
                if (e.Transaction?.TransactionInformation.Status != TransactionStatus.Committed)
                    return;

                // The completed transaction can still be the ambient one here, and the work must not join it.
                using (new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled))
                    foreach (var queuedAction in work.Actions)
                        queuedAction();
            };
            return;
        }

        action();
    }

    private bool AfterCommitInterceptorAdded => afterCommitInterceptorAdded ??=
        this.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()?.Interceptors?
            .Contains(ShiftDbContextTransactionInterceptor.Instance) == true;

    internal void OnTransactionStarted(Guid transactionId)
    {
        ResetIfNewLease();
        afterCommit = null;
        afterCommitTransaction = transactionId;
    }

    internal void OnTransactionCommitted(Guid transactionId)
    {
        ResetIfNewLease();
        var actions = afterCommit;
        var owner = afterCommitTransaction;
        afterCommit = null;
        afterCommitTransaction = null;

        // Only the committing transaction's own work runs.
        if (actions is null || owner != transactionId)
            return;

        foreach (var action in actions)
            action();
    }

    internal void OnTransactionEnded()
    {
        ResetIfNewLease();
        afterCommit = null;
        afterCommitTransaction = null;
    }

    // The rows a save writes are the entries EF accepts once the save succeeds: Added and Modified become Unchanged,
    // Deleted becomes Detached. Collecting them then, rather than before the save, includes the rows a before-save
    // step added. A save that does not accept its changes (acceptAllChangesOnSuccess: false) reports nothing, and a
    // save that no observer listens to collects nothing.
    private List<SavedRow>? StartCollecting(bool acceptAllChangesOnSuccess, IShiftDbContextSaveObserver[] listeners)
    {
        if (!acceptAllChangesOnSuccess || listeners.Length == 0)
        {
            savedRows = null;
            return null;
        }

        if (collectingSaves++ == 0)
            ChangeTracker.StateChanged += OnStateChangedInSave;

        return savedRows = new List<SavedRow>();
    }

    private void StopCollecting()
    {
        if (--collectingSaves == 0)
            ChangeTracker.StateChanged -= OnStateChangedInSave;
    }

    // Runs inside EF, for every state change while a save that collects is writing. It only takes notes: no observer,
    // rule or handler is called from here, because code that saves or throws inside EF's accept loop corrupts the save.
    private void OnStateChangedInSave(object? sender, EntityStateChangedEventArgs e)
    {
        if (savedRows is null)
            return;

        // A row that becomes modified during the save (EF's own change detection, a before-save step of a trigger
        // library, the owner of an owned type) still holds its values from before here. Once EF accepts it they are
        // gone, so this is the last moment to copy them.
        if (e.NewState == EntityState.Modified)
        {
            if (TypesNeedingOriginals.Length > 0 && NeedsOriginal(e.Entry.Metadata.ClrType))
                (savedOriginals ??= new(ReferenceEqualityComparer.Instance)).TryAdd(e.Entry.Entity, e.Entry.OriginalValues.ToObject());

            return;
        }

        // Only these changes are EF accepting a write. Any other, such as a new row that a before-save step stopped
        // tracking (Added to Detached), wrote nothing.
        SavedRowChange? change = (e.OldState, e.NewState) switch
        {
            (EntityState.Added, EntityState.Unchanged) => SavedRowChange.Added,
            (EntityState.Modified, EntityState.Unchanged) => SavedRowChange.Modified,
            (EntityState.Deleted, EntityState.Detached) => SavedRowChange.Deleted,
            _ => null,
        };

        if (change is null)
            return;

        savedRows.Add(new SavedRow(e.Entry.Entity, e.Entry.Metadata.ClrType, change.Value)
        {
            Original = change == SavedRowChange.Modified ? savedOriginals?.GetValueOrDefault(e.Entry.Entity) : null,
        });
    }

    // Copies what the rows that are already modified hold from before the save, for the types that need it. Only those
    // types are looked at, and no change detection runs here: a row that turns out to be modified when EF detects the
    // changes, or that a step inside the save changes, is copied at that moment (see OnStateChangedInSave).
    private Dictionary<object, object>? CaptureOriginals()
    {
        if (TypesNeedingOriginals.Length == 0)
            return null;

        Dictionary<object, object>? originals = null;
        var detect = ChangeTracker.AutoDetectChangesEnabled;
        ChangeTracker.AutoDetectChangesEnabled = false;

        try
        {
            foreach (var type in TypesNeedingOriginals)
                foreach (var entry in TrackedEntriesOf(type))
                    if (entry.State == EntityState.Modified)
                        (originals ??= new(ReferenceEqualityComparer.Instance))[entry.Entity] = entry.OriginalValues.ToObject();
        }
        finally
        {
            ChangeTracker.AutoDetectChangesEnabled = detect;
        }

        return originals;
    }

    // A save made from inside this context's own write (an after-save step of EF's pipeline, such as a trigger library's)
    // is about to write again. The observers of the outer save haven't run yet, and they must read what the outer save
    // wrote, so the rows they compare are copied now: EF still holds exactly that as each row's original values. Costs
    // nothing in a host whose code saves again from a save handler, which runs after the observers.
    private void KeepWhatWasWritten(List<SavedRow> rows)
    {
        if (rows.Count == 0 || TypesNeedingOriginals.Length == 0)
            return;

        var detect = ChangeTracker.AutoDetectChangesEnabled;
        ChangeTracker.AutoDetectChangesEnabled = false;

        try
        {
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];

                if (row.Change == SavedRowChange.Deleted || row.Written is not null || !NeedsOriginal(row.EntityType))
                    continue;

                var entry = Entry(row.Entity);

                if (entry.State is EntityState.Unchanged or EntityState.Modified)
                    rows[i] = row with { Written = entry.OriginalValues.ToObject() };
            }
        }
        finally
        {
            ChangeTracker.AutoDetectChangesEnabled = detect;
        }
    }

    // The model's entity types that an observer needs values from before the save for, worked out once per context.
    private Type[] TypesNeedingOriginals =>
        typesNeedingOriginals ??= OriginalsObservers.Length == 0
            ? []
            : Model.GetEntityTypes()
                .Select(type => type.ClrType)
                .Distinct()
                .Where(NeedsOriginal)
                .ToArray();

    private bool NeedsOriginal(Type entityType)
    {
        needsOriginal ??= [];

        if (needsOriginal.TryGetValue(entityType, out var needs))
            return needs;

        foreach (var observer in OriginalsObservers)
        {
            if (observer.NeedsOriginalOf(entityType))
            {
                needs = true;
                break;
            }
        }

        return needsOriginal[entityType] = needs;
    }

    // ChangeTracker.Entries<T>() picks the entries of one type before wrapping them, so the rows of other types cost
    // a type check each and nothing more. It is generic, so each type gets a cached delegate.
    private static readonly ConcurrentDictionary<Type, Func<ChangeTracker, IEnumerable<EntityEntry>>> entriesOfType = new();

    private static readonly MethodInfo entriesOfMethod =
        typeof(ShiftDbContext).GetMethod(nameof(EntriesOf), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static IEnumerable<EntityEntry> EntriesOf<TEntity>(ChangeTracker tracker) where TEntity : class => tracker.Entries<TEntity>();

    private IEnumerable<EntityEntry> TrackedEntriesOf(Type entityType) =>
        entriesOfType.GetOrAdd(entityType, type => entriesOfMethod.MakeGenericMethod(type).CreateDelegate<Func<ChangeTracker, IEnumerable<EntityEntry>>>())(ChangeTracker);

    private async ValueTask ReportSavedAsync(SaveReport report, IShiftDbContextSaveObserver[] listeners, CancellationToken cancellationToken)
    {
        foreach (var observer in listeners)
            await observer.SavedAsync(this, report, cancellationToken);
    }

    // The new signals become events once the rows they belong to are committed: at once when no transaction is open,
    // and when the open one commits otherwise. A rollback drops them.
    private void PublishAttentionAfterCommit(AttentionSave attention)
    {
        var services = ServicesOfSave;
        var dispatcher = services.GetService<IAttentionDispatcher>();

        if (dispatcher is null)
            return;

        // The window that made the change is read now, while the request is current: the fan-out runs later, in the
        // background, where it can no longer be read.
        var events = attention.CreateEvents(services.GetService<IAttentionOriginProvider>()?.OriginConnectionId);

        if (events.Count == 0)
            return;

        var logger = services.GetService<ILoggerFactory>()?.CreateLogger(typeof(AttentionSave).FullName!);

        AfterCommit(() => AttentionSave.Publish(dispatcher, events, logger));
    }

    // What runs before a save writes, in one pass over the tracked rows: the audit backfill, and the attention
    // evaluators of the rows the save is about to write. Returns the attention work left for after the write, and
    // whether the pass ran, which is when it has detected the changes.
    private async ValueTask<(AttentionSave? Attention, bool Detected)> PrepareBeforeWriteAsync(
        bool acceptAllChangesOnSuccess, bool async, CancellationToken cancellationToken)
    {
        AttentionOptions? attentionOptions = null;

        // A save that doesn't accept its changes may be sent again by its caller, so nothing is evaluated for it, as
        // nothing is reported for it.
        if (acceptAllChangesOnSuccess && !AttentionSuppressed && ModelHasAttention)
        {
            attentionOptions = ServicesOfSave.GetService<IOptions<AttentionOptions>>()?.Value ?? new AttentionOptions();

            if (attentionOptions.EvaluateOn == AttentionSaves.Repository && repositorySaves == 0)
                attentionOptions = null;
        }

        if (AuditStampingSuppressed && attentionOptions is null)
            return (null, false);

        var rows = StampAuditColumnsAndFindAttention(findAttention: attentionOptions is not null);

        if (rows is null)
            return (null, true);

        return (await AttentionSave.EvaluateAsync(this, rows, ServicesOfSave, attentionOptions!, async, cancellationToken), true);
    }

    /// <summary>
    /// Backfills the audit columns and creation claims on every <b>inserted</b> auditable row that has not already
    /// been stamped — the fallback for entities added directly through the context rather than through a repository.
    /// Each value fills only where the caller left it unset, so a manually-assigned value always wins. (A repository
    /// both stamps the rows and holds a <see cref="SuppressAuditStamping"/> scope around its save, so
    /// repository-routed saves skip this entirely, and the <c>AuditFieldsAreSet</c> guard skips any individually
    /// pre-stamped row.)
    ///
    /// <para>Deliberately insert-only. Rows that are merely <b>modified</b> through a bare context are left
    /// untouched: in practice those saves are system writes — sync jobs, enrichment, replication bookkeeping —
    /// where silently advancing <c>LastSaveDate</c> and overwriting <c>LastSavedByUserID</c> (with null, in a
    /// background scope) would destroy real audit information. User-facing updates flow through
    /// <see cref="ShiftRepository{DB,EntityType,ListDTO,ViewAndUpsertDTO}"/>, whose sweep stamps them; a
    /// non-repository update path that genuinely wants stamping opts in explicitly via
    /// <see cref="AuditStamper.StampAuditFields"/> (see <c>AttentionPipeline.ClearSignals</c> for the pattern).</para>
    ///
    /// <para>Every value is resolved <b>defensively</b>: if the claim service isn't registered, or a value is absent
    /// or can't be read, it is treated as null/default rather than throwing — a bare context (migrations, a unit test
    /// without DI) still saves, just without a user/org stamp.</para>
    ///
    /// <para>The same pass collects the added and modified rows that take part in attention, when the save evaluates
    /// them, so that a save walks its tracked rows once for both. It returns those rows, or null when there are none.</para>
    /// </summary>
    private List<EntityEntry>? StampAuditColumnsAndFindAttention(bool findAttention)
    {
        // A repository has already stamped these rows, or an infrastructure save opted out.
        var stamp = !AuditStampingSuppressed;

        var now = DateTimeOffset.UtcNow;

        var resolved = false;
        long? userId = null, countryId = null, regionId = null, cityId = null, companyId = null, branchId = null;
        List<EntityEntry>? attention = null;

        foreach (var entry in ChangeTracker.Entries())
        {
            var state = entry.State;

            // The rows this save writes that take part in attention. Their evaluators run after this pass, so they see
            // the stamped row. A row that changed in attention's own columns only is not a changed record.
            if (findAttention && entry.Entity is IHasAttention &&
                (state == EntityState.Added || (state == EntityState.Modified && !AttentionSave.IsOnlyBookkeeping(entry))))
                (attention ??= []).Add(entry);

            if (!stamp || state != EntityState.Added)
                continue; // insert-only by design — see the remarks above

            if (entry.Entity is not IShiftEntityAudit auditable || auditable.AuditFieldsAreSet)
                continue; // not auditable, or already stamped upstream (e.g. by the repository)

            // Resolve the acting user's claims once, only when there is actually a row to stamp.
            if (!resolved)
            {
                var claims = TryResolveClaims();
                userId = SafeClaim(claims, c => c.GetUserID());
                countryId = SafeClaim(claims, c => c.GetCountryID());
                regionId = SafeClaim(claims, c => c.GetRegionID());
                cityId = SafeClaim(claims, c => c.GetCityID());
                companyId = SafeClaim(claims, c => c.GetCompanyID());
                branchId = SafeClaim(claims, c => c.GetCompanyBranchID());
                resolved = true;
            }

            AuditStamper.StampAuditFields(auditable, isAdded: true, userId, now);
            AuditStamper.StampCreationClaims(entry.Entity, countryId, regionId, cityId, companyId, branchId);
        }

        return attention;
    }

    /// <summary>Resolves <see cref="IdentityClaimProvider"/> if available, returning null instead of throwing.</summary>
    private IdentityClaimProvider? TryResolveClaims()
    {
        try { return this.GetService<IdentityClaimProvider>(); }
        catch { return null; }
    }

    private static long? SafeClaim(IdentityClaimProvider? claims, Func<IdentityClaimProvider, long?> get)
    {
        if (claims is null)
            return null;

        try { return get(claims); }
        catch { return null; }
    }
}
