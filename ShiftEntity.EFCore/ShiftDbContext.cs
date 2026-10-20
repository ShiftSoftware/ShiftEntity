using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.EFCore.Replication;
using Microsoft.Extensions.Options;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Tagging;
using ShiftSoftware.ShiftEntity.EFCore.Tagging;
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

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
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
    /// <see cref="IShiftDbContextSaveObserver"/>s, so nothing is replicated for them: no document is written and no
    /// watermark changes. Use it for saves that must not reach another store, such as a development seeder or an
    /// import that a later catch-up replicates in bulk. Disposal restores the previous state, so scopes nest safely.
    /// </summary>
    /// <remarks>
    /// What a later dirty-only catch-up replicates: a row whose save advanced its <c>LastSaveDate</c>, as a repository
    /// save does, stays dirty, and the roots of replication trees are still marked as not replicated when such a save
    /// changes them or a row of their tree (see <c>HasReplicationTree</c>). Any other row the save changed, such as one
    /// edited through a bare context, looks replicated afterwards and needs a forced catch-up.
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

    // The observers, resolved once per context from its application service provider. Empty without DI.
    private IShiftDbContextSaveObserver[]? saveObservers;

    private IShiftDbContextSaveObserver[] SaveObservers =>
        saveObservers ??= _applicationServiceProvider?.GetServices<IShiftDbContextSaveObserver>().ToArray() ?? [];

    // The rows the save in progress has written. A save made from inside another save (a host trigger saving from its
    // after-save step) collects its own rows and puts the outer save's list back when it ends. The change tracker is
    // watched only while a save that collects runs: a pooled context loses its handlers when it goes back to the pool,
    // and a row whose state changes outside a save was not saved.
    private List<SavedRow>? savedRows;
    private int collectingSaves;

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
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess) =>
        SaveCoreAsync(acceptAllChangesOnSuccess, async: false, default).GetAwaiter().GetResult();

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default) =>
        SaveCoreAsync(acceptAllChangesOnSuccess, async: true, cancellationToken);

    // The save both overrides share. With async false the context's own calls are the synchronous ones. What an
    // observer does asynchronously is waited for, as the trigger library did for its after-save step.
    private async Task<int> SaveCoreAsync(bool acceptAllChangesOnSuccess, bool async, CancellationToken cancellationToken)
    {
        ResetIfNewLease();

        var outer = savedRows;
        var collecting = false;
        IDbContextTransaction? own = null;
        var savepoints = false;

        try
        {
            StampAuditColumns();

            // The roots of replication trees are marked whenever the context declares trees, also when nothing is
            // replicated now (under SuppressReplication, without an observer, or when the changes are not accepted),
            // so that a later catch-up rebuilds them. The audit stamping has just detected the changes, unless it was
            // suppressed.
            var trees = ReplicationTreeSave.Prepare(this, detectChanges: AuditStampingSuppressed);

            if (trees is { NeedsTransaction: true })
            {
                RefuseRetryingStrategy();

                // Marking a tree's roots must commit or roll back with the save, so such a save runs in a transaction:
                // the caller's, an ambient or enlisted one, or else one the context opens itself.
                //
                // LOUD: EF accepts the save's changes inside base.SaveChanges, before the roots are marked and before
                // this transaction commits. When the mark, an observer (a preparer), the caller's cancellation or the
                // commit fails after that, SQL rolls back but the context's rows say Unchanged, so a retry on the same
                // context saves nothing. That is why a retrying execution strategy is refused above. After such a
                // failure the caller must start again on a new context.
                if (CanOpenOwnTransaction())
                {
                    own = async ? await Database.BeginTransactionAsync(cancellationToken) : Database.BeginTransaction();
                    OnTransactionStarted(own.TransactionId);

                    // EF would set a savepoint before writing, which nothing here rolls back to: a failure rolls the
                    // whole transaction back.
                    savepoints = Database.AutoSavepointsEnabled;
                    Database.AutoSavepointsEnabled = false;
                }
            }

            var rows = StartCollecting(acceptAllChangesOnSuccess);
            collecting = rows is not null;

            int result;
            try
            {
                result = async
                    ? await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken)
                    : base.SaveChanges(acceptAllChangesOnSuccess);
            }
            finally
            {
                if (collecting)
                {
                    collecting = false;
                    StopCollecting();
                }
                savedRows = outer;
            }

            var changedRoots = trees is null ? NoRoots : await trees.FindAndMarkRootsAsync(this, async, cancellationToken);

            if (rows is not null && (rows.Count > 0 || changedRoots.Count > 0))
                await ReportSavedAsync(new SaveReport(rows, changedRoots), cancellationToken);

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
    // step added. A save that does not accept its changes (acceptAllChangesOnSuccess: false) reports nothing.
    private List<SavedRow>? StartCollecting(bool acceptAllChangesOnSuccess)
    {
        if (ReplicationSuppressed || !acceptAllChangesOnSuccess || SaveObservers.Length == 0)
        {
            savedRows = null;
            return null;
        }

        if (collectingSaves++ == 0)
            ChangeTracker.StateChanged += CollectSavedRow;

        return savedRows = new List<SavedRow>();
    }

    private void StopCollecting()
    {
        if (--collectingSaves == 0)
            ChangeTracker.StateChanged -= CollectSavedRow;
    }

    private void CollectSavedRow(object? sender, EntityStateChangedEventArgs e)
    {
        if (savedRows is null)
            return;

        // Only these changes are EF accepting a write. Any other, such as a new row that a before-save step stopped
        // tracking (Added to Detached), wrote nothing.
        SavedRowChange? change = (e.OldState, e.NewState) switch
        {
            (EntityState.Added, EntityState.Unchanged) => SavedRowChange.Added,
            (EntityState.Modified, EntityState.Unchanged) => SavedRowChange.Modified,
            (EntityState.Deleted, EntityState.Detached) => SavedRowChange.Deleted,
            _ => null,
        };

        if (change is not null)
            savedRows.Add(new SavedRow(e.Entry.Entity, e.Entry.Metadata.ClrType, change.Value));
    }

    private async ValueTask ReportSavedAsync(SaveReport report, CancellationToken cancellationToken)
    {
        foreach (var observer in SaveObservers)
            await observer.SavedAsync(this, report, cancellationToken);
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
    /// </summary>
    private void StampAuditColumns()
    {
        if (AuditStampingSuppressed)
            return; // a repository already stamped these rows, or an infrastructure save opted out

        var now = DateTimeOffset.UtcNow;

        var resolved = false;
        long? userId = null, countryId = null, regionId = null, cityId = null, companyId = null, branchId = null;

        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State != EntityState.Added)
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
