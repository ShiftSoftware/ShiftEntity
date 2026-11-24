using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ShiftSoftware.ShiftEntity.EFCore.Replication;
using Microsoft.Extensions.Options;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Attention;
using ShiftSoftware.ShiftEntity.Core.Tagging;
using ShiftSoftware.ShiftEntity.EFCore.Attention;
using ShiftSoftware.ShiftEntity.EFCore.Tagging;
using System.Data;

namespace ShiftSoftware.ShiftEntity.EFCore;

public abstract partial class ShiftDbContext : DbContext
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

    // The options carry the framework's own extension, through which the context checks them when EF first builds its
    // services for them (see SaveLifecycleOptionsExtension).
    public ShiftDbContext(DbContextOptions options) : base(SaveLifecycleOptionsExtension.AddTo(options))
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

        // A context built with the parameterless constructor gets the framework's extension here.
        if (optionsBuilder.Options.FindExtension<SaveLifecycleOptionsExtension>() is null)
            ((IDbContextOptionsBuilderInfrastructure)optionsBuilder).AddOrUpdateExtension(SaveLifecycleOptionsExtension.Instance);

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
    /// The save handlers that belong to this context's model: the ones that keep its data right. They run in every
    /// host that uses the context, with nothing for the host to register, for every save that writes a row of their
    /// type. Called once per model and host, like <see cref="OnModelCreating"/>. EF builds one model for each context
    /// type and database provider, so a context may declare a handler for one database only. An override calls the
    /// base method.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A handler that belongs to one host is added there instead, with <c>services.AddSaveHandler&lt;THandler&gt;()</c>,
    /// and a handler of a module that contributes entity types to the context is declared with the module
    /// (<see cref="IModelBuildingContributor.ConfigureSaveHandlers"/>). Among handlers with the same order, the context's
    /// run first, then the modules', then the host's.
    /// </para>
    /// <para>
    /// A handler declared here is created from the services of the saving context, with its constructor injected. A
    /// handler whose only constructor takes nothing needs no services: it is created with <c>new</c> and runs on any
    /// context, and it asks for what it needs through its rows (<see cref="RowSet{TEntity}.Services"/>). One that
    /// takes services cannot be created by a context that has none, such as one a tool builds with <c>new</c>, so
    /// that context refuses to save a row that has such a handler: it is given services with
    /// <see cref="AttachServices"/>, or the save names the handler it means to skip
    /// (<see cref="SuppressSaveHandler{THandler}"/>).
    /// </para>
    /// <para>
    /// A handler declared here is a new instance for each stage call, unless the host registers the class, which makes
    /// it one instance per scope (<c>AddSaveHandler</c>). So a handler keeps what it passes from one stage to a later one
    /// in its rows' <see cref="RowSet{TEntity}.Items"/>, not in its fields.
    /// </para>
    /// </remarks>
    protected virtual void ConfigureSaveHandlers(SaveHandlerSet handlers)
    {
    }

    internal void DeclareSaveHandlers(SaveHandlerSet handlers) => ConfigureSaveHandlers(handlers);

    private ShiftDatabaseFacade? database;

    /// <summary>
    /// The same <c>Database</c> as always. A transaction begun through it is followed: every save made in it shares one
    /// before-commit and one after-commit stage, which run inside its <c>Commit</c>, and its rollback drops what waited
    /// for the commit. See <see cref="ShiftDatabaseFacade"/> for the transactions the context cannot follow.
    /// </summary>
    public override ShiftDatabaseFacade Database
    {
        get
        {
            // EF's own facade says whether the context is disposed, and keeps the two settings a pool restores.
            _ = base.Database;
            return database ??= new ShiftDatabaseFacade(this);
        }
    }

    /// <summary>EF's own facade, which holds <c>AutoTransactionBehavior</c> and <c>AutoSavepointsEnabled</c>.</summary>
    internal DatabaseFacade EfDatabase => base.Database;

    // A transaction left open goes with the context: EF rolls it back, and what waited for its commit is dropped.
    public override void Dispose()
    {
        database?.Abandon();
        base.Dispose();
    }

    public override ValueTask DisposeAsync()
    {
        database?.Abandon();
        return base.DisposeAsync();
    }

    // ── The services of a save ──────────────────────────────────────────────────

    private IServiceProvider? attachedServices;

    /// <summary>
    /// Gives the context the services its save handlers and the framework's parts are resolved from, for a context
    /// that did not come from a DI scope: one from a pool or a factory, whose own services are the root provider, and
    /// one built with <c>new</c>, which has none. Pass the provider of the scope the work runs in. A pooled context
    /// forgets them when it is leased again. They are also what a handler finds in
    /// <see cref="RowSet{TEntity}.Services"/>.
    /// </summary>
    public void AttachServices(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        ResetIfNewLease();
        attachedServices = services;
        ForgetWhatWasResolved();
    }

    // The services the handlers and the parts of a save come from: the ones that were attached, or else the scope the
    // context came from. Null for a context that was built without any.
    private IServiceProvider? SaveServices => attachedServices ?? _applicationServiceProvider;

    // The same, or EF's own when the context has none, for the parts that do without.
    private IServiceProvider ServicesOfSave => SaveServices ?? ((IInfrastructure<IServiceProvider>)this).Instance;

    // What a handler finds in its rows' Services: the services of a save. A context that has none gives a provider with
    // nothing registered, and not EF's own, so that a handler asks for a service of its host and gets no answer where
    // there is no host, and never gets hold of EF's internals.
    internal IServiceProvider HandlerServices
    {
        get
        {
            ResetIfNewLease();
            return SaveServices ?? NoServices.Provider;
        }
    }

    private static class NoServices
    {
        public static readonly IServiceProvider Provider = new ServiceCollection().BuildServiceProvider();
    }

    // Everything below is read once per context from its services, and again when other services are attached.
    private void ForgetWhatWasResolved()
    {
        saveObservers = null;
        originalsObservers = null;
        needsOriginal = null;
        saveHandlers = null;
        saveHandlersRead = false;
        servicesAreTheRoot = null;
        typeNeeds = null;
        lastType = null;
        lastNeeds = null;
    }

    // The observers, resolved once per context from its services, in the order they hear about a save: the
    // framework's observers that read what the save wrote, then the others as registered. The save handlers, which
    // may change the records and save again, run after them all. Empty without services.
    private IShiftDbContextSaveObserver[]? saveObservers;

    private IShiftDbContextSaveObserver[] SaveObservers =>
        saveObservers ??= SaveServices?.GetServices<IShiftDbContextSaveObserver>()
            .OrderBy(observer => observer is IOrderedSaveObserver ordered ? (int)ordered.Order : (int)SaveObserverOrder.Registered)
            .ToArray() ?? [];

    // The observers that hear about a save starting now, in their order, and which of the context's observers they
    // are. SuppressAfterCommit silences them all: they only do what waits for the commit. SuppressReplication silences
    // the plain observers, such as replication's. A framework observer with a switch of its own (the notification
    // rules) is silenced by that switch. Without any switch on, this is the list itself.
    private IShiftDbContextSaveObserver[] ListenersOfThisSave(SaveSwitches? switches, out ulong listening)
    {
        var observers = SaveObservers;
        listening = 0;

        if (observers.Length == 0 || switches is { AfterCommit: true })
            return [];

        var silenced = 0;

        for (var i = 0; i < observers.Length; i++)
        {
            if (IsSilenced(observers[i]))
                silenced++;
            else
                listening |= ObserverBit(i);
        }

        if (silenced == 0)
            return observers;

        return silenced == observers.Length ? [] : observers.Where(observer => !IsSilenced(observer)).ToArray();
    }

    private bool IsSilenced(IShiftDbContextSaveObserver observer) =>
        observer is ISwitchedSaveObserver switched ? switched.IsSuppressed(this) : ReplicationSuppressed;

    // One bit for each of the context's observers. A host with more observers than bits makes the rest share one.
    private static ulong ObserverBit(int index) => 1UL << Math.Min(index, 63);

    // The handlers of this context's model in its host: the ones the context declares, the ones its modules declare,
    // and the ones the host added. Read once per context. Null when there are none.
    private SaveHandlerRegistry? saveHandlers;
    private bool saveHandlersRead;

    internal SaveHandlerRegistry? SaveHandlers
    {
        get
        {
            if (!saveHandlersRead)
            {
                saveHandlersRead = true;
                saveHandlers = SaveHandlerRegistry.Of(this, SaveServices) is { IsEmpty: false } registry ? registry : null;
            }

            return saveHandlers;
        }
    }

    /// <summary>
    /// A handler is created from the services of the saving context, when it has rows. One the host registered comes
    /// from its container. Any other is created here and belongs to its call, which disposes it.
    /// </summary>
    internal object CreateHandler(SaveHandlerCall call, out bool owned) =>
        call.Handler.Create(SaveServices, SaveServices is not null && ServicesAreTheRoot, out owned);

    internal static async ValueTask DisposeHandlerAsync(object handler)
    {
        if (handler is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync();
        else if (handler is IDisposable disposable)
            disposable.Dispose();
    }

    // Whether the context's services are a root provider: those of a pooled context, and of one from a factory. The
    // root scope of a host is the scope factory that every one of its scopes hands out, so a provider that hands out
    // itself as that factory is the root.
    private bool? servicesAreTheRoot;

    private bool ServicesAreTheRoot => servicesAreTheRoot ??= SaveServices is { } services && IsTheRoot(services);

    private static bool IsTheRoot(IServiceProvider services)
    {
        try
        {
            return services.GetService<IServiceScopeFactory>() is { } root && ReferenceEquals(root, services.GetService<IServiceProvider>());
        }
        catch (Exception)
        {
            return false;
        }
    }

    // A handler that applies to a row the context is saving must be created, and skipping it would be silent. So a
    // save is refused, before anything runs for it, when a handler of its rows cannot be created: its constructor
    // needs services and the context has none, or the context's services are a root provider and the handler is not
    // a singleton. A handler would then be created outside any scope, and a scoped one, or the scoped services it
    // takes, would be kept for the life of the process. Scope validation catches that in Development only. A handler
    // whose only constructor takes nothing needs no services and no scope: it is created with new, and runs on any
    // context. What it needs it asks of its rows' Services, when it needs it.
    private void RefuseHandlersThatCannotBeCreated(TypeNeeds needs, SaveSwitches? switches)
    {
        var services = SaveServices;
        var every = true;

        foreach (var call in needs.Handlers.All)
        {
            if (SaveSwitches.IsOff(switches, call))
            {
                every = false;
                continue;
            }

            if (services is not null && !ServicesAreTheRoot)
                continue;

            if (call.Handler.CanBeCreatedWithoutAScope(anyServices: services is not null))
                continue;

            if (services is null)
                throw new InvalidOperationException(
                    $"{GetType().Name} is saving a {needs.ClrType.Name} row, which has the save handler {call.Name}, but the context has no " +
                    "services to create the handler from: it was built without a service provider, and the handler's constructor takes " +
                    "parameters. Give the context the services of the scope the work runs in with AttachServices(scope.ServiceProvider), " +
                    $"or switch the handler off for this save with SuppressSaveHandler<{call.HandlerType.Name}>() to say that the save " +
                    "means to skip it.");

            throw new InvalidOperationException(
                $"{GetType().Name} is saving a {needs.ClrType.Name} row, which has the save handler {call.Name}, but the context's services " +
                "are the root provider: it came from a pool or a factory, its options are a singleton, or it was resolved from the root " +
                "provider and not from a scope. The handler would be created outside any scope, and a scoped one, or the scoped " +
                "services it takes, would be kept for the life of the process. Give " +
                "the context the services of the scope the work runs in with AttachServices(scope.ServiceProvider), register the handler " +
                $"as a singleton and add it with AddSaveHandler, or switch it off for this save with SuppressSaveHandler<{call.HandlerType.Name}>().");
        }

        needs.HandlersCanBeCreated = every;
    }

    // The token of the steps after a commit. It is not the caller's: a request that is aborted after the commit must
    // not lose its email. It is cancelled only when the host is stopping.
    internal CancellationToken AfterCommitToken =>
        ServicesOfSave.GetService<IHostApplicationLifetime>()?.ApplicationStopping ?? CancellationToken.None;

    internal ILogger? LifecycleLogger
    {
        get
        {
            // Asked while the context goes away too, when the scope it came from is already gone. EF's own services
            // still hold the host's logger factory then.
            try
            {
                return this.GetService<ILoggerFactory>().CreateLogger(typeof(ShiftDbContext).FullName!);
            }
            catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException)
            {
                return null;
            }
        }
    }

    // ── The trigger library ─────────────────────────────────────────────────────

    private bool triggerLibraryChecked;

    /// <summary>
    /// Throws when the trigger library (EntityFrameworkCore.Triggered) is switched on for this context. EF runs the
    /// same check when it first builds its services for the context's options. This is for a context whose options did
    /// not get the framework's extension, and for the host's check at start, which must not wait for the first save.
    /// </summary>
    internal void CheckForTheTriggerLibrary()
    {
        if (triggerLibraryChecked)
            return;

        // The options as they are after OnConfiguring. Asking for them makes EF build its services for them, which
        // is when it validates the framework's extension. Options without the extension are checked here.
        var options = this.GetService<IDbContextOptions>();

        if (options.FindExtension<SaveLifecycleOptionsExtension>() is null)
            TriggerLibrary.Check(options);

        triggerLibraryChecked = true;
    }

    // ── A save in progress ──────────────────────────────────────────────────────

    // The part of a save that is running, where a second save on the context is refused.
    private enum SavePart : byte
    {
        None,
        BeforeSave,
        Write,
    }

    // One save in progress. A save made from inside another save has a frame of its own, with the outer one behind it.
    private sealed class SaveFrame(SaveFrame? outer)
    {
        public SaveFrame? Outer { get; } = outer;
        public int Depth { get; } = outer is null ? 0 : outer.Depth + 1;

        // What the save commits with: the transaction's unit, or its own. A save that needs no transaction gets one
        // only when something is queued in it.
        public SaveUnit? Unit;

        // Where the unit's log stood when the save began. A failed save cuts the log back to here. And how many
        // savepoints of the app's own the unit had then: a stage of the save must not roll back to one of those.
        public int LogStart;
        public int SavepointsAtStart;

        // A shallow copy of the unit's bag (Items) when the save began, which a failed save puts back. Null when the
        // bag was empty or the save had no unit yet.
        public Dictionary<object, object?>? ItemsAtStart;

        // Whether what the save wrote is committed: the write of a save without a transaction, or the commit of the
        // transaction the context opened for it. Before that a failure fails the save, after it nothing is undone.
        // A save inside a caller's transaction ends before that transaction commits.
        public bool Committed;

        public bool PastCommit => Committed || Unit is { IsOpen: false };

        // Whether the stages after the write run before the commit: in a transaction, or as if in one.
        public bool InTransaction;

        // In a transaction this context cannot follow, so nothing can wait for its commit. When that is an ambient or
        // an enlisted one, this is it.
        public bool Unseen;
        public System.Transactions.Transaction? Ambient;

        // The stages that belong inside a transaction run without one, because the provider has none. A save made
        // from inside such a save joins it.
        public bool WithoutTransaction;
        public bool Joined;

        // The transaction the context opened for this save.
        public ShiftDbContextTransaction? Own;

        // The framework's savepoint, set before the write in a transaction that is not this save's own.
        public IDbContextTransaction? SavepointOn;
        public string? Savepoint;

        // Whether a before-save handler asked for a transaction around this save (RequireTransactionAsync, TakeLockAsync)
        // inside one that is not the save's own. What the handler wrote from then on belongs to the save: where no
        // savepoint could be set, a failure before the write leaves it in that transaction, as a failure after the write
        // leaves the write.
        public bool AskedForTransaction;

        // Whether SQL was written, and one added or modified row that EF accepted.
        public bool Wrote;
        public WrittenRow? Written;

        // The handlers that are switched off for this save, as the switches stood when it started.
        public SaveSwitches? Switches;

        // What the repository that started this save told the context. Null for any other save, and for a save made
        // from inside a repository's. And what that repository does after the handlers that run after the write.
        public RepositorySave? Repository;
        public Func<Task>? AfterHandlers;

        // Whether the save was called asynchronously, for what the context itself does on its behalf.
        public bool Async;

        // The part of the save that is running, and the before-save handler that is.
        public SavePart Part;
        public SaveHandlerCall? BeforeSaveCall;

        // The before-save stage looked at every row the save is about to write, and none is of a type that an
        // observer wants or a handler after the write has.
        public bool NothingAfterTheWrite;

        // Makes the frame of a save that has ended as it was when it was new, so that the next save can have it.
        // Every field above is here. A test reads the frame after a save and fails when one is left out.
        public void Clear()
        {
            Unit = null;
            LogStart = 0;
            SavepointsAtStart = 0;
            ItemsAtStart = null;
            Committed = false;
            InTransaction = false;
            Unseen = false;
            Ambient = null;
            WithoutTransaction = false;
            Joined = false;
            Own = null;
            SavepointOn = null;
            Savepoint = null;
            AskedForTransaction = false;
            Wrote = false;
            Written = null;
            Switches = null;
            Repository = null;
            AfterHandlers = null;
            Async = false;
            Part = SavePart.None;
            BeforeSaveCall = null;
            NothingAfterTheWrite = false;
        }
    }

    private SaveFrame? saveFrame;

    // The frame of the context's last save that was not made from inside another, for the next such save. Nothing
    // keeps a save's frame once the save has ended, and most saves are small: the frame was half of what a save
    // with nothing to write allocated.
    private SaveFrame? spareFrame;

    // The unit whose before-commit or after-commit stage is running. A stage that starts inside another puts the
    // outer one back when it ends.
    private SaveUnit? stageUnit;

    internal SaveUnit? EnterStage(SaveUnit unit)
    {
        var outer = stageUnit;
        stageUnit = unit;
        return outer;
    }

    internal void LeaveStage(SaveUnit? outer) => stageUnit = outer;

    // The stage methods that are running, outermost first. A save made from inside one does not call it again for a
    // record it is handling.
    private List<RunningHandler>? running;

    internal void Running(RunningHandler handler) => (running ??= []).Add(handler);

    internal void Ran(RunningHandler handler) => running!.Remove(handler);

    internal bool IsHandling(SaveHandlerCall call, in SavedRow row)
    {
        if (running is null)
            return false;

        foreach (var handler in running)
            if (handler.Call == call && handler.Handles(Model, row))
                return true;

        return false;
    }

    // The rows the save in progress has written, from the first one EF accepts. A save made from inside a handler of
    // another collects its own rows and puts the outer save's list back when it ends. The change tracker is watched
    // only while a save that collects is writing: a pooled context loses its handlers when it goes back to the pool,
    // and a row whose state changes outside a save was not saved.
    private List<SavedRow>? savedRows;
    private int collectingSaves;
    private EventHandler<EntityStateChangedEventArgs>? stateChangedInSave;

    // What the write in progress collects, and one added or modified row that EF accepted in it.
    private struct Collecting
    {
        public bool On;
        public ulong Listening;                 // the observers that hear about the save
        public bool ForHandlers;                // the rows of the types that have handlers after the write
        public SaveSwitches? Switches;          // the handlers that are switched off for the save
        public bool Before;                     // what the modified and deleted rows held before
        public int Expected;                    // how many rows the look before the write counted, to size the list once
        public WrittenRow? Written;
    }

    private Collecting collecting;

    // What a modified or deleted row of the save in progress held before it, by tracked instance, for the observers
    // and handlers that asked. A nested save keeps its own and puts the outer save's back, as with savedRows.
    private struct RowBefore
    {
        public object? Original;
        public SavedRowFlags Flags;
    }

    private Dictionary<object, RowBefore>? rowsBefore;

    private ISavedRowOriginals[]? originalsObservers;
    private Dictionary<Type, bool>? needsOriginal;

    private ISavedRowOriginals[] OriginalsObservers =>
        originalsObservers ??= SaveObservers.OfType<ISavedRowOriginals>().ToArray();

    // How many rows the context's saves have collected for the observers and the handlers, and how many copies of old
    // values they have made. What a save costs beyond EF's own work shows here, so the cost tests read them.
    internal long RowsCollected { get; private set; }
    internal long OriginalsCopied { get; private set; }

    // What the saves of this context need to know about one entity type of its model. Worked out once per type.
    private sealed class TypeNeeds
    {
        public required Type ClrType { get; init; }

        // The handlers that get the type's rows. None for an owned type: its owner is the row.
        public required HandlerPlan Handlers { get; init; }

        // Whether every handler of the type can be created from the context's services. Asked when a row of the type
        // is first saved, and again as long as a switch kept a handler from being asked about.
        public bool HandlersCanBeCreated;

        // The observers that want the type's rows, one bit for each of the context's observers.
        public ulong Wanted { get; init; }

        // Whether an observer or a handler compares a modified row with what it held before the save.
        public bool CopyModified { get; init; }

        // Whether a handler reads what a deleted row held.
        public bool CopyDeleted { get; init; }

        // The column behind IsSoftDelete, when handlers get the type's rows after the write.
        public IProperty? IsDeleted { get; init; }

        // Whether the type's rows have audit columns to stamp.
        public bool IsAuditable { get; init; }

        // For an owned type, the type whose row its entries are part of.
        public TypeNeeds? Owner { get; init; }

        // Whether several entity types share the type's CLR type, as the join rows of a many-to-many do.
        public bool IsShared { get; init; }

        // The navigations to the owned parts of the type's rows. Null when it has none.
        public INavigation[]? OwnedNavigations { get; init; }
    }

    private Dictionary<IEntityType, TypeNeeds>? typeNeeds;
    private IEntityType? lastType;
    private TypeNeeds? lastNeeds;

    private TypeNeeds NeedsOf(IEntityType type)
    {
        if (ReferenceEquals(type, lastType))
            return lastNeeds!;

        typeNeeds ??= [];

        if (!typeNeeds.TryGetValue(type, out var needs))
            typeNeeds[type] = needs = BuildNeeds(type);

        lastType = type;
        return lastNeeds = needs;
    }

    private TypeNeeds BuildNeeds(IEntityType type)
    {
        var clrType = type.ClrType;
        var ownership = type.FindOwnership();
        var handlers = ownership is null && SaveHandlers is { } registry ? registry.For(clrType) : HandlerPlan.None;
        var owned = type.GetNavigations().Where(navigation => navigation.ForeignKey.IsOwnership && !navigation.IsOnDependent).ToArray();

        ulong wanted = 0;
        var observers = SaveObservers;

        for (var i = 0; i < observers.Length; i++)
            if (observers[i].Wants(this, clrType))
                wanted |= ObserverBit(i);

        return new TypeNeeds
        {
            ClrType = clrType,
            Handlers = handlers,
            Wanted = wanted,
            CopyModified = handlers.NeedsOriginal || NeedsOriginal(clrType),
            CopyDeleted = handlers.NeedsOriginal,
            IsDeleted = handlers.AnyAfterWrite && type.FindProperty(nameof(IShiftEntityAudit.IsDeleted)) is { } property && property.ClrType == typeof(bool) ? property : null,
            IsAuditable = typeof(IShiftEntityAudit).IsAssignableFrom(clrType),
            Owner = ownership is null ? null : TopOwner(ownership.PrincipalEntityType),
            OwnedNavigations = owned.Length == 0 ? null : owned,
            IsShared = type.HasSharedClrType,
        };
    }

    private TypeNeeds TopOwner(IEntityType type)
    {
        while (type.FindOwnership() is { } ownership)
            type = ownership.PrincipalEntityType;

        return typeNeeds!.TryGetValue(type, out var needs) ? needs : typeNeeds[type] = BuildNeeds(type);
    }

    // Whether the model has a type that takes part in attention. Worked out once per model, read once per context.
    private bool? modelHasAttention;

    private bool ModelHasAttention => modelHasAttention ??= AttentionSave.AppliesTo(Model);

    // The lease of a pooled context that the state above belongs to.
    private int lease;

    // A pooled context goes back to the pool and is leased again as the same instance. EF resets its own state then;
    // this context's state of an earlier lease, such as a transaction that never committed, a suppression scope never
    // disposed or the services that were attached, is dropped when the new lease first uses the context.
    internal void ResetIfNewLease()
    {
        var current = ContextId.Lease;
        if (current == lease)
            return;

        lease = current;
        database?.Abandon();
        saveFrame = null;
        stageUnit = null;
        running = null;
        undone = null;
        switchScopes = null;
        AuditStampingSuppressed = false;
        ReplicationSuppressed = false;
        NotificationsSuppressed = false;
        AttentionSuppressed = false;
        switches = null;
        repositorySaves = 0;
        startingRepositorySave = null;

        if (attachedServices is not null)
        {
            attachedServices = null;
            ForgetWhatWasResolved();
        }
    }

    // Waits for what observers and handlers do asynchronously. The caller's synchronization context is cleared
    // meanwhile, so a handler that awaits without ConfigureAwait(false) carries on on the thread pool and cannot
    // deadlock against the thread that waits here.
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        var captured = SynchronizationContext.Current;

        if (captured is null)
            return SaveCoreAsync(acceptAllChangesOnSuccess, async: false, default).GetAwaiter().GetResult();

        SynchronizationContext.SetSynchronizationContext(null);

        try
        {
            return SaveCoreAsync(acceptAllChangesOnSuccess, async: false, default).GetAwaiter().GetResult();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(captured);
        }
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default) =>
        SaveCoreAsync(acceptAllChangesOnSuccess, async: true, cancellationToken);

    // The save both overrides share. With async false the context's own calls are the synchronous ones. What a
    // handler or a part does asynchronously is waited for.
    //
    // Before save: in rounds, audit stamping and the before-save handlers, for the rows that are new in each round.
    // Then once, on the rows as the handlers left them: the repository's own step, the attention evaluators, and what
    // the replication trees need. Then the context looks at the rows the save is about to write, and decides whether
    // the save needs a transaction: for replication trees, for the signals of new rows, or because an after-save or a
    // before-commit handler has those rows. It opens one itself when none is open, and earlier when a before-save
    // handler asks.
    //
    // The write: EF's own save, with its own change detection, during which the context only takes notes of the rows
    // EF accepts.
    //
    // After the write, in a transaction: the signals that waited for a new row's key, the roots of replication trees,
    // the observers (notification rules, replication), the after-save handlers, then the repository's reload. A
    // failure fails the save, and what it wrote is undone. The context's own transaction then commits: before commit,
    // the commit, after commit. A caller's transaction commits later, in its Commit.
    //
    // After the write, without a transaction: the write was the commit. The observers, the repository's reload, the
    // queued work and the after-commit handlers all run, whatever fails, and the failures leave as one
    // AfterCommitException.
    private async Task<int> SaveCoreAsync(bool acceptAllChangesOnSuccess, bool async, CancellationToken cancellationToken)
    {
        ResetIfNewLease();

        if (!triggerLibraryChecked)
            CheckForTheTriggerLibrary();

        RefuseAfterAnUndoneSave();
        RefuseInATransactionThatHasEnded();

        var outer = saveFrame;

        if (outer is not null)
            RefuseToSaveInside(outer);

        SaveFrame frame;

        if (outer is null && spareFrame is { } spare)
        {
            spareFrame = null;
            frame = spare;
        }
        else
        {
            frame = new SaveFrame(outer);
        }

        frame.Switches = switches;
        frame.Repository = TakeRepositorySave();
        frame.Async = async;

        var outerRows = savedRows;
        var outerBefore = rowsBefore;
        var outerCollecting = collecting;
        var collects = false;

        saveFrame = frame;

        try
        {
            // Where the save stands, settled before anything can queue work in it: in a transaction this context
            // follows, in one it cannot follow, or inside a save that runs its stages without one.
            var transaction = Database.Followed;
            var unseen = transaction is null ? UnseenTransaction(out frame.Ambient) : null;

            if (transaction is not null)
            {
                frame.Unit = transaction.Unit;
                frame.InTransaction = true;
            }
            else if (unseen is not null)
            {
                frame.Unseen = frame.InTransaction = true;
            }
            else if (outer is { PastCommit: false } && (outer.WithoutTransaction || outer.Joined))
            {
                frame.Unit = outer.Unit;
                frame.Joined = frame.InTransaction = true;
            }

            frame.LogStart = frame.Unit?.Position ?? 0;
            frame.SavepointsAtStart = frame.Unit?.Savepoints ?? 0;
            frame.ItemsAtStart = frame.Unit?.SnapshotItems();

            var (attention, detected, hasChanges) = await RunBeforeSaveAsync(frame, acceptAllChangesOnSuccess, cancellationToken);

            // The roots of replication trees are marked whenever the context declares trees, also when nothing is
            // replicated now (under SuppressReplication or SuppressAfterCommit, without an observer, or when the
            // changes are not accepted), so that a later catch-up rebuilds them. The before-save stage has detected
            // the changes, unless nothing asked for it.
            var trees = ReplicationTreeSave.Prepare(this, detectChanges: !detected, out var treesLooked);
            detected |= treesLooked;

            // The observers this save is reported to and the handlers that run after its write, decided as it starts:
            // the switches on now apply to it.
            var listeners = ListenersOfThisSave(frame.Switches, out var listening);
            var handlers = SaveHandlers is { HasAfterWrite: true } registry ? registry : null;
            var reports = acceptAllChangesOnSuccess && (listeners.Length > 0 || handlers is not null);

            // The transaction decision needs the rows as the write will find them. So do the two refusals below.
            var detect = !detected && handlers is not null &&
                (handlers.HasInTransactionStages || !acceptAllChangesOnSuccess || unseen is not null);

            // A save that the context's pass found nothing to write for has nothing to look at.
            rowsBefore = null;
            var plan = hasChanges == false ? new SavePlan { HasChanges = false }
                : frame.NothingAfterTheWrite ? new SavePlan { HasChanges = true }
                : LookAtRowsBeforeWrite(frame.Switches, handlers, listening, copies: reports, detect, detected || detect);

            if (!acceptAllChangesOnSuccess)
                RefuseWorkAfterAWriteThatIsNotAccepted(plan, listeners, trees, detected || detect);

            if (unseen is not null)
                RefuseWorkThatWaitsForTheCommit(unseen, plan, listeners, attention, trees, detected || detect, frame.Switches);

            // Marking a tree's roots must commit or roll back with the save. So must the signals of new rows of an
            // indexed attention type, which are written second, and whatever an after-save or a before-commit handler
            // does. Such a save runs in a transaction: the caller's, or else one the context opens itself. A provider
            // without transactions, such as the in-memory one, has none to open for the signals and the handlers.
            var needsTransaction = trees is { NeedsTransaction: true } ||
                ((attention is { HasPendingRows: true } || plan.InTransaction) && Database.IsRelational());

            if (!frame.InTransaction && needsTransaction)
            {
                // LOUD: EF accepts the save's changes inside base.SaveChanges, before the steps after the write and
                // before this transaction commits. When one of those fails, SQL rolls back but the context's rows say
                // Unchanged, so a retried attempt would save nothing. That is why a retrying execution strategy is
                // refused here, and why the context refuses its next save until it is cleared (see RememberUndone).
                RefuseATransactionOfItsOwn(
                    trees is { NeedsTransaction: true } ? "rows of a replication tree, whose roots it marks after the write"
                    : plan.InTransaction ? $"{plan.Handled?.Name} rows, which have an after-save or a before-commit handler"
                    : "new rows whose attention signals it writes after them");

                frame.Own = await Database.BeginForSaveAsync(UnitOf(frame), async, cancellationToken);
                frame.InTransaction = true;
            }
            else if (!frame.InTransaction && plan.InTransaction)
            {
                // The provider has no transactions. The stages that belong inside one still run, in their order.
                frame.WithoutTransaction = frame.InTransaction = true;
            }

            // The repository's reload, which runs after the handlers.
            var reload = frame.AfterHandlers;

            // What can still fail the save once it has written.
            var failsAfterWrite = (acceptAllChangesOnSuccess && (listeners.Length > 0 || plan.AfterSave)) ||
                attention is { HasPendingRows: true } || trees is not null || reload is not null;

            // In a transaction that is not this save's own, such a failure must undo this save alone. The framework's
            // savepoint takes the place of EF's, which only covers the write. A save that is known to write nothing
            // sets none, as EF sets none for it. One that a before-save handler set already, where it asked for a
            // transaction, is the one the write uses.
            if (failsAfterWrite && plan.HasChanges != false)
                await SetSavepointAsync(frame, async, cancellationToken);

            // The context listens to EF's state changes during the write whenever something acts after it, also for a
            // save that looks empty: an interceptor of the app's may still add a row. In a transaction it listens for
            // one row at least, to remember if the save is undone.
            List<SavedRow>? rows = null;
            savedRows = null;

            if (reports || (acceptAllChangesOnSuccess && frame.InTransaction && failsAfterWrite))
            {
                StartCollecting();
                collects = true;
            }

            collecting = new Collecting
            {
                On = collects,
                Listening = collects && reports ? listening : 0,
                ForHandlers = collects && reports && handlers is not null,
                Switches = frame.Switches,
                Before = collects && (handlers is not null || OriginalsObservers.Length > 0),
                Expected = plan.Wanted,
            };

            int result;
            var wroteTwice = false;
            var heldSavepoint = Database.HoldEfSavepoint;

            try
            {
                // Again, because a before-save handler may have ended it with SQL of its own, and gone on.
                RefuseInATransactionThatHasEnded();

                // A save that starts on this context while EF writes is refused: it would write this save's rows.
                frame.Part = SavePart.Write;

                // EF would set a savepoint of its own before writing. Where the framework's is set it is not needed,
                // and in the context's own transaction nothing rolls back to one: a failure rolls it all back.
                Database.HoldEfSavepoint = frame.Savepoint is not null || frame.Own is not null;

                result = async
                    ? await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken)
                    : base.SaveChanges(acceptAllChangesOnSuccess);

                frame.Wrote = true;

                // The new rows have their keys now, so the signals that waited for them are written, in the same
                // transaction. The count the caller gets stays the count of its own rows.
                if (attention is { HasPendingRows: true })
                {
                    attention.AddPendingRows(this);
                    wroteTwice = true;

                    if (async)
                        await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
                    else
                        base.SaveChanges(acceptAllChangesOnSuccess);
                }
            }
            finally
            {
                Database.HoldEfSavepoint = heldSavepoint;
                frame.Part = SavePart.None;
                frame.Written = collecting.Written;
                rows = savedRows;

                if (collects)
                {
                    collects = false;
                    StopCollecting();
                }
                savedRows = outerRows;
                rowsBefore = outerBefore;
                collecting = outerCollecting;
            }

            // The new signals become events once the save is committed, unless nobody is to be told.
            if (attention is not null && frame.Switches is not { AfterCommit: true })
                PublishAttentionAfterCommit(attention);

            var handled = handlers is not null && rows is { Count: > 0 };

            if (frame.InTransaction)
            {
                var changedRoots = trees is null ? NoRoots : await trees.FindAndMarkRootsAsync(this, async, cancellationToken);

                // Before anything that may save again: the log keeps the saves in the order they wrote.
                if (!frame.Unseen)
                    AppendToUnit(frame, rows, handlers, wroteTwice);

                if (acceptAllChangesOnSuccess && listeners.Length > 0 && (rows is { Count: > 0 } || changedRoots.Count > 0))
                    await ReportSavedAsync(new SaveReport(rows ?? NoRows, changedRoots), listeners, cancellationToken);

                if (handled)
                    await UnitOf(frame).RunAfterSaveAsync(handlers!, rows!, wroteTwice, frame.Switches, cancellationToken);

                if (reload is not null)
                    await reload();

                await ReleaseSavepointAsync(frame, async, cancellationToken);

                if (frame.Own is not null)
                {
                    // Before commit, the commit, after commit.
                    await frame.Own.CommitForSaveAsync(async, cancellationToken);
                }
                else if (frame.WithoutTransaction)
                {
                    var unit = UnitOf(frame);
                    await unit.RunBeforeCommitAsync(cancellationToken);
                    frame.Committed = true;
                    await unit.CompleteAfterCommitAsync(unit.EnterAfterCommit());
                }

                return result;
            }

            // Without a transaction the write was the commit.
            frame.Committed = true;

            if (frame.Unit is null && !handled && trees is null && reload is null && !(listeners.Length > 0 && rows is { Count: > 0 }))
                return result;

            var own = UnitOf(frame);
            AppendToUnit(frame, rows, handlers, wroteTwice);
            var outerStage = own.EnterAfterCommit();

            try
            {
                var changedRoots = trees is null ? NoRoots : await trees.FindAndMarkRootsAsync(this, async, CancellationToken.None);

                if (acceptAllChangesOnSuccess && listeners.Length > 0 && (rows is { Count: > 0 } || changedRoots.Count > 0))
                {
                    var report = new SaveReport(rows ?? NoRows, changedRoots);
                    var token = AfterCommitToken;

                    foreach (var observer in listeners)
                    {
                        try
                        {
                            await observer.SavedAsync(this, report, token);
                        }
                        catch (Exception failure)
                        {
                            own.Fail(failure);
                        }
                    }
                }

                if (reload is not null)
                {
                    try
                    {
                        await reload();
                    }
                    catch (Exception failure)
                    {
                        own.Fail(failure);
                    }
                }

                if (handled)
                    ReportHandlersThatNeededATransaction(own, handlers!, rows!, frame.Switches);
            }
            catch (Exception failure)
            {
                own.Fail(failure);
            }

            await own.CompleteAfterCommitAsync(outerStage);

            return result;
        }
        catch
        {
            // Once the rows are committed nothing is undone: the failure is the report. So it is when the commit itself
            // failed, where nobody knows whether the rows are there.
            if (!frame.PastCommit)
                await UndoAsync(frame, async);

            throw;
        }
        finally
        {
            // Also when the save failed before it wrote anything, for example when its transaction could not begin.
            if (collects)
                StopCollecting();
            savedRows = outerRows;
            rowsBefore = outerBefore;
            collecting = outerCollecting;
            saveFrame = outer;

            if (frame.Own is { } own)
            {
                if (async)
                    await own.DisposeAsync();
                else
                    own.Dispose();
            }

            // The save has ended, whichever way. Its frame is kept for the next save that starts on its own.
            if (outer is null)
            {
                frame.Clear();
                spareFrame = frame;
            }
        }
    }

    // A second save on the context is refused where it would write the rows of the save in progress, and where saves
    // from inside saves never end.
    private void RefuseToSaveInside(SaveFrame outer)
    {
        if (outer.Part == SavePart.BeforeSave)
            throw new InvalidOperationException(
                $"{GetType().Name} cannot save from inside the before-save stage of a save that is in progress on it" +
                $"{(outer.BeforeSaveCall is { } call ? $" ({call.Name})" : "")}. That save is about to write the tracked rows, so a save from " +
                "here would write them first and leave it with nothing to write. Change, add or remove tracked rows and let the save in " +
                "progress write them. A write that must commit on its own goes through another context.");

        if (outer.Part == SavePart.Write)
            throw new InvalidOperationException(
                $"{GetType().Name} cannot save from inside EF's own write of a save that is in progress on it: an interceptor or an event " +
                "handler of the app's is saving again. Such a save would write the rows of the save in progress. Add or change rows before " +
                "the save or in a before-save handler, and save again from an after-save or an after-commit handler.");

        if (outer.Depth >= SaveUnit.Limit)
            throw new InvalidOperationException(
                $"A save of {GetType().Name} was started from inside another save, {SaveUnit.Limit + 1} saves deep. Handlers whose saves start " +
                $"handlers that save again never end. The handlers that are running: {string.Join(", then ", (running ?? []).Select(handler => handler.Call.Name))}.");
    }

    private static readonly IReadOnlyDictionary<Type, IReadOnlyCollection<long>> NoRoots = new Dictionary<Type, IReadOnlyCollection<long>>();

    private static readonly IReadOnlyList<SavedRow> NoRows = [];

    private SaveUnit UnitOf(SaveFrame frame) => frame.Unit ??= new SaveUnit(this);

    // What the rows a save is about to write mean for the stages after the write.
    private struct SavePlan
    {
        public bool AfterSave;
        public bool BeforeCommit;
        public bool AfterCommit;

        // One entity type among the rows that has a handler after the write, and one that has a handler at the commit.
        public Type? Handled;
        public Type? AtCommit;

        // Whether the save has rows to write. Null when nobody has looked, or changes were not detected yet.
        public bool? HasChanges;

        // How many rows the look counted that an observer that listens or a handler after the write has.
        public int Wanted;

        public readonly bool InTransaction => AfterSave || BeforeCommit;
    }

    // One pass over the tracked rows before the write, and only in a host that has handlers after the write or an
    // observer that compares: which stages have handlers for the rows the save is about to write, and what the modified
    // and deleted ones held before. Without detect it detects nothing, and a row that only EF's own detection finds is
    // noted at that moment (see OnStateChangedInSave).
    private SavePlan LookAtRowsBeforeWrite(SaveSwitches? switches, SaveHandlerRegistry? handlers, ulong listening, bool copies, bool detect, bool detected)
    {
        var plan = new SavePlan();

        if (handlers is null && !(copies && OriginalsObservers.Length > 0))
            return plan;

        var any = false;
        var autoDetect = ChangeTracker.AutoDetectChangesEnabled;

        try
        {
            if (!detect)
                ChangeTracker.AutoDetectChangesEnabled = false;

            foreach (var entry in ChangeTracker.Entries())
            {
                var state = entry.State;

                if (state is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
                    continue;

                any = true;
                var needs = NeedsOf(entry.Metadata);
                var wanted = (needs.Wanted & listening) != 0;

                // An owned entry is part of its owner's row. It needs a look only when the owner's type has a handler
                // after the write (when nothing else of the owner changed, EF marks the owner modified only during the
                // write, and the plan must know of it), or when an observer wants the owned type or compares its
                // values with those from before.
                if (needs.Owner is { } owner && !owner.Handlers.AnyAfterWrite && !wanted && !(copies && needs.CopyModified))
                    continue;

                if (handlers is not null)
                {
                    var row = needs.Owner ?? needs;
                    var calls = row.Handlers;

                    if (calls.AnyAfterWrite)
                    {
                        if (!row.HandlersCanBeCreated)
                            RefuseHandlersThatCannotBeCreated(row, switches);

                        var afterSave = SaveSwitches.AnyOn(switches, calls.For(SaveStage.AfterSave));
                        var beforeCommit = SaveSwitches.AnyOn(switches, calls.For(SaveStage.BeforeCommit));
                        var afterCommit = SaveSwitches.AnyOn(switches, calls.For(SaveStage.AfterCommit));

                        if (afterSave || beforeCommit || afterCommit)
                        {
                            // The handlers keep the owner's row, never an owned entry: an owned entry is counted only
                            // when an observer wants its type.
                            wanted |= needs.Owner is null;
                            plan.Handled ??= row.ClrType;
                            plan.AfterSave |= afterSave;
                            plan.BeforeCommit |= beforeCommit;
                            plan.AfterCommit |= afterCommit;

                            if (beforeCommit || afterCommit)
                                plan.AtCommit ??= row.ClrType;
                        }
                    }
                }

                if (wanted)
                    plan.Wanted++;

                if (copies && state != EntityState.Added)
                    NoteBefore(entry, state, needs, detected);
            }
        }
        finally
        {
            ChangeTracker.AutoDetectChangesEnabled = autoDetect;
        }

        plan.HasChanges = any ? true : detected ? false : (bool?)null;
        return plan;
    }

    // Copies what a modified or deleted row held before the save, for the types that need it. Once EF accepts the row
    // the values are gone.
    private void NoteBefore(EntityEntry entry, EntityState state, TypeNeeds needs, bool detected)
    {
        var before = new RowBefore();

        if (state == EntityState.Modified ? needs.CopyModified : needs.CopyDeleted)
        {
            before.Original = entry.OriginalValues.ToObject();
            OriginalsCopied++;
        }

        // Read from the values, not from IsModified alone: a row that EF is only now finding modified has not had
        // every property looked at yet. A row that was modified before the write has, and there an unmodified column
        // needs no second look, which is every row but the few that are being deleted or restored.
        if (state == EntityState.Modified && needs.IsDeleted is { } isDeleted)
        {
            var property = entry.Property(isDeleted);

            if ((!detected || property.IsModified) &&
                property.OriginalValue is bool was && property.CurrentValue is bool now && was != now)
                before.Flags = now ? SavedRowFlags.SoftDeleted : SavedRowFlags.Restored;
        }

        if (before.Original is not null || before.Flags != SavedRowFlags.None)
            (rowsBefore ??= new(ReferenceEqualityComparer.Instance)).TryAdd(entry.Entity, before);
    }

    // A save keeps a record in its unit's log when its transaction will commit later, or when a handler has its rows
    // at the commit. Only the rows of types with such a handler are kept, and only for a handler that is not switched
    // off for the save.
    private void AppendToUnit(SaveFrame frame, List<SavedRow>? rows, SaveHandlerRegistry? handlers, bool wroteTwice)
    {
        var kept = handlers is not null && rows is { Count: > 0 } ? RowsForTheCommit(handlers, rows, frame.Switches) : null;

        if (kept is null && (frame.Written is null || frame.PastCommit))
            return;

        UnitOf(frame).Append(new SaveLogItem
        {
            Rows = kept,
            RowsRepeat = wroteTwice,
            Written = frame.Written,
            MadeBy = running is { Count: > 0 } ? [.. running] : null,
            Switches = kept is null ? null : frame.Switches,
        });
    }

    private static List<SavedRow>? RowsForTheCommit(SaveHandlerRegistry handlers, List<SavedRow> rows, SaveSwitches? switches)
    {
        List<SavedRow>? some = null;
        var kept = 0;
        Type? type = null;
        var atCommit = false;

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];

            if (row.EntityType != type)
            {
                type = row.EntityType;
                var plan = handlers.For(type);
                atCommit = SaveSwitches.AnyOn(switches, plan.For(SaveStage.BeforeCommit)) || SaveSwitches.AnyOn(switches, plan.For(SaveStage.AfterCommit));
            }

            if (atCommit && !row.IsOwnedPart)
            {
                some?.Add(row);
                kept++;
                continue;
            }

            // The first row that is left out: from here on the kept rows go to a list of their own.
            some ??= rows.GetRange(0, i);
        }

        return kept == 0 ? null : some ?? rows;
    }

    // A row with a handler that must run inside the write's transaction, in a save that had none: the row first
    // appeared during EF's write (an interceptor added it, or a cascade EF resolved there), after the save had decided
    // that it needed no transaction. The row is committed, so the handler cannot run in its transaction any more.
    private void ReportHandlersThatNeededATransaction(SaveUnit unit, SaveHandlerRegistry handlers, List<SavedRow> rows, SaveSwitches? switches)
    {
        HashSet<Type>? reported = null;
        Type? type = null;

        foreach (var row in rows)
        {
            if (row.EntityType == type || row.IsOwnedPart)
                continue;

            type = row.EntityType;
            var plan = handlers.For(type);

            var calls = plan.For(SaveStage.AfterSave).Concat(plan.For(SaveStage.BeforeCommit))
                .Where(call => !SaveSwitches.IsOff(switches, call))
                .Select(call => call.Name)
                .ToList();

            if (calls.Count == 0 || !(reported ??= []).Add(type))
                continue;

            unit.Fail(new InvalidOperationException(
                $"A {type.Name} row was saved by {GetType().Name} without its handlers that run inside the save's transaction: {string.Join(", ", calls)}. " +
                "The row first appeared during EF's write, after the save had found no row that needed a transaction, and it is committed. " +
                "Add or change such a row before the save or in a before-save handler, not in an interceptor or through a cascade that EF " +
                "resolves while it writes."));
        }
    }

    private string[]? savepointNames;

    // One name for each depth of nesting, so that saves in a long transaction don't pile savepoints up. The context's
    // part keeps two contexts that share one transaction apart.
    private string SavepointName(int depth) =>
        (savepointNames ??= new string[SaveUnit.Limit + 1])[depth] ??= $"__ShiftSave{depth}_{ContextId.InstanceId.GetHashCode():x8}";

    // Sets the framework's savepoint of a save, once, in a transaction that is not the save's own: the caller's, one
    // the context follows or one joined with UseTransaction, or the transaction of the save that this save was made
    // from. A failure of the save rolls back to it. The save's own transaction rolls back whole and needs none, and an
    // ambient or an enlisted transaction has none to set: a failure there can only roll the whole transaction back.
    // Nor is one set where the transaction has no savepoints or the app turned them off.
    private async ValueTask SetSavepointAsync(SaveFrame frame, bool async, CancellationToken cancellationToken)
    {
        if (frame.Savepoint is not null || frame.Own is not null)
            return;

        var target = frame.Unit?.Transaction?.Inner ?? (frame.Unseen ? Database.EfCurrentTransaction : null);

        if (target is null || !target.SupportsSavepoints || !Database.SavepointsAllowed)
            return;

        var name = SavepointName(frame.Depth);

        if (async)
            await target.CreateSavepointAsync(name, cancellationToken);
        else
            target.CreateSavepoint(name);

        frame.SavepointOn = target;
        frame.Savepoint = name;
    }

    // The save got through its stages, so its savepoint is not needed any more. A provider that cannot release one
    // does nothing here.
    private static async ValueTask ReleaseSavepointAsync(SaveFrame frame, bool async, CancellationToken cancellationToken)
    {
        if (frame.Savepoint is not { } name)
            return;

        frame.Savepoint = null;

        if (async)
            await frame.SavepointOn!.ReleaseSavepointAsync(name, cancellationToken);
        else
            frame.SavepointOn!.ReleaseSavepoint(name);
    }

    // The save failed before its commit, so the database must be as it was before the save. Its own transaction is
    // rolled back. Inside another transaction it is rolled back to the savepoint. Where there is no savepoint, that
    // transaction can only be rolled back as a whole, when the save left something in it: its write, or what a
    // before-save handler wrote after it asked for a transaction. This never throws: the first failure is the one to
    // report.
    private async ValueTask UndoAsync(SaveFrame frame, bool async)
    {
        var undone = false;
        var leftSomething = frame.Wrote || frame.AskedForTransaction;

        if (frame.Own is { } own)
        {
            try
            {
                if (async)
                    await own.RollbackAsync(fromItsSave: true, CancellationToken.None);
                else
                    own.Rollback(fromItsSave: true);
            }
            catch
            {
                // Disposing the transaction ends it anyway.
            }

            undone = frame.Wrote;
        }
        else if (frame.Savepoint is { } name)
        {
            frame.Savepoint = null;

            try
            {
                if (async)
                {
                    await frame.SavepointOn!.RollbackToSavepointAsync(name, CancellationToken.None);
                    await frame.SavepointOn.ReleaseSavepointAsync(name, CancellationToken.None);
                }
                else
                {
                    frame.SavepointOn!.RollbackToSavepoint(name);
                    frame.SavepointOn.ReleaseSavepoint(name);
                }
            }
            catch
            {
                // The server has already rolled the whole transaction back (a deadlock victim, an aborted batch), or the
                // connection is gone.
                frame.Unit?.MarkRollbackOnly("a save in it failed, and it could not be rolled back to where that save began. The database may have ended it.");
            }

            undone = frame.Wrote;
        }
        else if (leftSomething && frame.Unit?.Transaction is not null)
        {
            frame.Unit.MarkRollbackOnly(frame.Wrote
                ? "a save in it failed after its write, and without a savepoint that write cannot be undone alone."
                : "a save in it failed after a before-save handler asked for a transaction around it (RequireTransactionAsync or TakeLockAsync), and " +
                  "without a savepoint what that handler wrote since cannot be undone alone.");
            undone = frame.Wrote;
        }
        else if (leftSomething && frame.Ambient is { } ambient)
        {
            // No savepoint can be set in an ambient or an enlisted transaction, so it can only be rolled back as a whole.
            try
            {
                ambient.Rollback();
            }
            catch
            {
                // It has ended already.
            }

            undone = frame.Wrote;
        }

        if (frame.Unit is { Transaction: not null } unit)
        {
            // The log and the bag go back to where they were when the save began.
            unit.CutTo(frame.LogStart, frame.SavepointsAtStart);
            unit.RestoreItems(frame.ItemsAtStart);

            if (frame.Own is null && unit.Transaction.EndedOutsideEf)
                unit.MarkRollbackOnly("the database ended it when a save in it failed.");
        }

        if (undone)
            RememberUndone(frame.Written);
    }

    // One added or modified row of a save that was rolled back after EF had accepted it. The context still shows such
    // rows as saved, with their keys, so its next save would write nothing for them and report no error.
    private WrittenRow? undone;

    internal void RememberUndone(WrittenRow? written)
    {
        if (written is not { } row)
            return;

        // One row is remembered. A row of a type with its own CLR type is preferred to a shared-type row, as within a
        // save.
        if (undone is { } known)
        {
            if (known.Shared && !row.Shared)
                undone = row;

            return;
        }

        undone = row;
        ChangeTracker.Tracked += OnTrackedAfterAnUndoneSave;
    }

    // The row is tracked anew: the app stopped tracking it, or cleared the tracker, and is starting over with it.
    private void OnTrackedAfterAnUndoneSave(object? sender, EntityTrackedEventArgs e)
    {
        if (undone is { } row && ReferenceEquals(e.Entry.Entity, row.Row))
            ForgetUndone();
    }

    private void ForgetUndone()
    {
        undone = null;
        ChangeTracker.Tracked -= OnTrackedAfterAnUndoneSave;
    }

    // A save is refused while the remembered row is still tracked, in any state. The row is looked for among the tracked
    // entries by reference, which creates no entry and throws for none: EF's Entry(object) throws for a shared-type row
    // that is no longer tracked, such as a join row after ChangeTracker.Clear(). A row that is no longer tracked is
    // forgotten. Strict on purpose: the context remembers one row of the undone save, so it cannot tell that every row
    // of that save was given back when that one is added or changed again.
    private void RefuseAfterAnUndoneSave()
    {
        if (undone is not { } row)
            return;

        var autoDetect = ChangeTracker.AutoDetectChangesEnabled;
        IEntityType? tracked = null;

        try
        {
            ChangeTracker.AutoDetectChangesEnabled = false;

            foreach (var entry in ChangeTracker.Entries())
            {
                if (ReferenceEquals(entry.Entity, row.Row))
                {
                    tracked = entry.Metadata;
                    break;
                }
            }
        }
        finally
        {
            ChangeTracker.AutoDetectChangesEnabled = autoDetect;
        }

        if (tracked is null)
        {
            ForgetUndone();
            return;
        }

        throw new InvalidOperationException(
            $"{GetType().Name} holds rows of a save that was rolled back, a row of {tracked.DisplayName()} among them. The context still shows " +
            "them as saved, so saving again would write nothing for them and report no error. Call ChangeTracker.Clear(), detach every row of " +
            "that save, or use a new context. Adding or changing the same instances again does not end this: the context cannot tell that every " +
            "row of that save was given back. A save that is tried again after such a failure starts from a new context, or from a cleared " +
            "change tracker, on each attempt.");
    }

    // A save that needs a transaction of the context's own is refused, before it writes, where that transaction
    // cannot do its job. Under an execution strategy that retries, the context's own or one that runs the save as part
    // of a larger operation: EF accepts the rows before the commit, so a retried attempt would find nothing left to
    // save and lose them without an error. While a strategy runs one, every strategy says that it does not retry, so
    // the running one is asked too. And with AutoTransactionBehavior.Never: the app has said no transactions.
    private void RefuseATransactionOfItsOwn(string why)
    {
        IExecutionStrategy? retrying = ExecutionStrategy.Current is { MaxRetryCount: > 0 } executing
            ? executing
            : Database.CreateExecutionStrategy() is { RetriesOnFailure: true } strategy ? strategy : null;

        if (retrying is not null)
            throw new InvalidOperationException(
                $"{GetType().Name} cannot save {why}, under an execution strategy that retries on failure ({retrying.GetType().Name}). " +
                "Such a save runs in a transaction the context opens itself, and EF accepts its changes before that transaction commits, " +
                "so a retried attempt would find nothing to save and lose the changes without an error. Configure this context without a " +
                "retrying execution strategy, for example without EnableRetryOnFailure, or begin a transaction through Database inside the " +
                "strategy's Execute and redo the changes on each attempt.");

        if (Database.AutoTransactionBehavior == AutoTransactionBehavior.Never)
            throw new InvalidOperationException(
                $"{GetType().Name} cannot save {why}, with AutoTransactionBehavior.Never: such a save must run in a transaction, and this " +
                "context was told to open none. Begin a transaction through Database before the save, or leave AutoTransactionBehavior as it is.");
    }

    // The database has rolled the open transaction back (a deadlock victim, an aborted batch), or the app has ended it
    // through the ADO.NET object, and EF still holds it. ADO.NET runs a command outside any transaction then, so what a
    // save writes would be committed on its own, while the app takes it for a part of the rest. Where EF or the framework
    // sets a savepoint first, that fails. Where none is set (MARS, savepoints switched off, the save's own transaction),
    // nothing would.
    private void RefuseInATransactionThatHasEnded()
    {
        if (Database.Followed is { EndedOutsideEf: true })
            throw new InvalidOperationException(
                $"{GetType().Name} cannot save: the transaction it is in has ended already, in the database (a deadlock victim, an aborted batch) " +
                "or through the ADO.NET object, so what this save writes would be committed on its own. Roll the transaction back, and start over " +
                "in a new one.");
    }

    /// <summary>
    /// The isolation level of the transaction that is open on this context, one it follows or one it cannot follow. Null
    /// when there is none, and when the one EF holds has ended without EF, in the database or through the ADO.NET object.
    /// </summary>
    internal IsolationLevel? IsolationOfTheOpenTransaction()
    {
        if (Database.CurrentTransaction is { } current)
        {
            var transaction = current.GetDbTransaction();

            return transaction.Connection is null ? null : transaction.IsolationLevel;
        }

        // An ambient or an enlisted one.
        if (UnseenTransaction(out var ambient) is null || ambient is not { TransactionInformation.Status: System.Transactions.TransactionStatus.Active })
            return null;

        return ambient.IsolationLevel == System.Transactions.IsolationLevel.Snapshot ? IsolationLevel.Snapshot : IsolationLevel.Unspecified;
    }

    private ITransactionEnlistmentManager? enlistment;
    private bool enlistmentRead;

    // A transaction this context is in and cannot follow, as a message names it: its commit never reaches the context.
    // Null when there is none. Asked only when no followed transaction is open.
    private string? UnseenTransaction() => UnseenTransaction(out _);

    private string? UnseenTransaction(out System.Transactions.Transaction? ambient)
    {
        ambient = null;

        if (Database.EfCurrentTransaction is not null)
            return "a transaction that was not begun through this context's Database: one joined with UseTransaction, or begun with " +
                "BeginTransaction(IsolationLevel) through a variable typed DbContext";

        if (!enlistmentRead)
        {
            enlistmentRead = true;
            enlistment = this.GetService<IDbContextTransactionManager>() as ITransactionEnlistmentManager;
        }

        if (enlistment is null)
            return null;

        if ((ambient = enlistment.CurrentAmbientTransaction) is not null)
            return "an ambient transaction (TransactionScope)";

        return (ambient = enlistment.EnlistedTransaction) is not null ? "a transaction the connection is enlisted in (EnlistTransaction)" : null;
    }

    private InvalidOperationException NotFollowed(string transaction, string what, string fix) => new(
        $"{GetType().Name} {what} inside {transaction}. The context is not told when that transaction commits, so the work could only run " +
        $"too early, for rows that may never be committed, or never. Begin the transaction through this context's Database (BeginTransaction), " +
        $"which the context follows{fix}.");

    // Inside a transaction the context cannot follow, a save is refused while nothing is written when it has work that
    // waits for the commit: a before-commit or an after-commit handler, an attention event, or what a framework
    // observer queues for its rows (replication, a notification rule). A save that tells nobody
    // (SuppressAfterCommit) has only its before-commit handlers left to wait.
    private void RefuseWorkThatWaitsForTheCommit(string transaction, SavePlan plan, IShiftDbContextSaveObserver[] listeners,
        AttentionSave? attention, ReplicationTreeSave? trees, bool detected, SaveSwitches? switches)
    {
        if (plan.AtCommit is { } type)
            throw NotFollowed(transaction, $"cannot save {type.Name} rows, which have a before-commit or an after-commit handler,",
                ", or switch those handlers off for this save (SuppressSaveHandler<THandler>() for one of them, SuppressSaveHandlers(stages) for a stage)");

        if (attention is { HasNewSignals: true } && switches is not { AfterCommit: true } && ServicesOfSave.GetService<IAttentionDispatcher>() is not null)
            throw NotFollowed(transaction, "cannot save rows that raise an attention signal, whose event waits for the commit,",
                ", or save without telling anybody (SuppressAfterCommit())");

        if (FindWorkOfAnObserver(listeners, trees, detected) is var (waits, waiting) && waits is not null)
            throw NotFollowed(transaction, $"cannot save {waits.Name} rows, for which {waiting!.Work} waits for the commit,",
                $", or switch that work off for this save ({waiting.Switch}, or SuppressAfterCommit() to tell nobody)");
    }

    // A save that does not accept its changes is refused, while nothing is written, when something after the write
    // applies to its rows: after such a save a record does not hold its new key, and EF raises no event for the rows it
    // wrote, so no handler could run for them and no observer could be told. Before save has run by now, and a save
    // with nothing after its write is EF's own.
    private void RefuseWorkAfterAWriteThatIsNotAccepted(SavePlan plan, IShiftDbContextSaveObserver[] listeners, ReplicationTreeSave? trees, bool detected)
    {
        const string because = "After such a save a record does not hold its new key and EF reports no row as written, so nothing after the write could work on the rows. Save with SaveChanges()";

        if (plan.Handled is { } handled)
            throw new InvalidOperationException(
                $"{GetType().Name} cannot save {handled.Name} rows with acceptAllChangesOnSuccess: false: save handlers that run after the write " +
                $"apply to them. {because}, or switch those handlers off for this save (SuppressSaveHandler<THandler>() for one of them, " +
                "SuppressSaveHandlers(stages) for a stage).");

        if (FindWorkOfAnObserver(listeners, trees, detected) is var (waits, waiting) && waits is not null)
            throw new InvalidOperationException(
                $"{GetType().Name} cannot save {waits.Name} rows with acceptAllChangesOnSuccess: false: {waiting?.Work ?? "a part of the framework"} " +
                $"applies to them after the write. {because}, or switch that work off for this save ({waiting?.Switch ?? "SuppressAfterCommit()"}).");
    }

    // One entity type among the rows the save is about to write that an observer that listens has work for, and the
    // observer when it says what waits.
    private (Type? Type, IWaitsForCommitSaveObserver? Observer) FindWorkOfAnObserver(IShiftDbContextSaveObserver[] listeners, ReplicationTreeSave? trees, bool detected)
    {
        List<Type>? written = null;

        foreach (var listener in listeners)
        {
            written ??= TypesAboutToBeWritten(detected);

            // An observer that says what it waits for is asked exactly. Any other is taken to have work for the rows it
            // wants.
            if (listener is IWaitsForCommitSaveObserver waiting)
            {
                var waits = written.Find(candidate => waiting.WaitsFor(this, candidate, asChangedRoot: false))
                    ?? trees?.RootTypes().FirstOrDefault(root => waiting.WaitsFor(this, root, asChangedRoot: true));

                if (waits is not null)
                    return (waits, waiting);
            }
            else if (written.Find(candidate => listener.Wants(this, candidate)) is { } wanted)
            {
                return (wanted, null);
            }
        }

        return (null, null);
    }

    private List<Type> TypesAboutToBeWritten(bool detected)
    {
        var types = new List<Type>();
        var autoDetect = ChangeTracker.AutoDetectChangesEnabled;

        try
        {
            if (detected)
                ChangeTracker.AutoDetectChangesEnabled = false;

            foreach (var entry in ChangeTracker.Entries())
                if (entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted && !types.Contains(entry.Metadata.ClrType))
                    types.Add(entry.Metadata.ClrType);
        }
        finally
        {
            ChangeTracker.AutoDetectChangesEnabled = autoDetect;
        }

        return types;
    }

    /// <summary>A stage of a save in progress must not open a transaction on the saving context.</summary>
    internal void RefuseToBeginInsideASave()
    {
        ResetIfNewLease();

        if (saveFrame is { PastCommit: false } || stageUnit is { State: SaveUnitState.BeforeCommit })
            throw new InvalidOperationException(
                $"{GetType().Name} cannot begin a transaction from inside a save that is in progress on it. The stages after the write " +
                "already run in the save's transaction, and the context opens one itself for a save that needs it. A before-save handler " +
                "that needs one asks with RequireTransactionAsync. Otherwise begin the transaction before the save, or in a step that " +
                "runs after the commit.");
    }

    /// <summary>
    /// A transaction belongs to the save that runs in it, or to the code that began it: from inside a stage of that
    /// save it can be neither committed nor rolled back.
    /// </summary>
    internal void RefuseInsideItsSave(ShiftDbContextTransaction transaction, string ended)
    {
        var inItsSave = saveFrame is { PastCommit: false } frame && ReferenceEquals(frame.Unit, transaction.Unit);
        var inItsBeforeCommit = ReferenceEquals(stageUnit, transaction.Unit) && transaction.Unit.State == SaveUnitState.BeforeCommit;

        if (inItsSave || inItsBeforeCommit)
            throw new InvalidOperationException(
                $"A transaction of {GetType().Name} cannot be {ended} from inside a save that runs in it. It belongs to the save in progress, " +
                "or to the code that began it. Throw to fail the save, and let the code that began the transaction end it.");
    }

    /// <summary>
    /// A savepoint that was set before a save began holds none of that save: rolling back to it from inside a stage of
    /// the save would take the save's write away from under it.
    /// </summary>
    internal void RefuseToRollBackPastItsSave(ShiftDbContextTransaction transaction, string savepoint)
    {
        var unit = transaction.Unit;
        var index = unit.IndexOfSavepoint(savepoint);

        if (index < 0)
            return;

        var beforeItsSave = saveFrame is { PastCommit: false } frame && ReferenceEquals(frame.Unit, unit) && index < frame.SavepointsAtStart;
        var beforeItsCommit = ReferenceEquals(stageUnit, unit) && unit.State == SaveUnitState.BeforeCommit && index < unit.SavepointsBeforeCommit;

        if (beforeItsSave || beforeItsCommit)
            throw new InvalidOperationException(
                $"A transaction of {GetType().Name} cannot be rolled back to the savepoint '{savepoint}' from inside a save that runs in it: " +
                "the savepoint was set before that save began. Throw to fail the save, and let the code that set the savepoint roll back to it.");
    }

    /// <summary>
    /// The same for a transaction this context cannot follow, where it can be asked through <see cref="Database"/>:
    /// the save in progress runs in it.
    /// </summary>
    internal void RefuseToEndATransactionInsideASave(string ended)
    {
        if (saveFrame is { PastCommit: false, Unseen: true })
            throw new InvalidOperationException(
                $"The transaction a save of {GetType().Name} runs in cannot be {ended} from inside that save. It belongs to the code that " +
                "began it. Throw to fail the save.");
    }

    /// <summary>
    /// Runs the work once the rows saved so far are committed: at once when no save and no transaction is in progress,
    /// otherwise when what is in progress commits. A rollback drops it. The work may use this context.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Inside a save that needs no transaction, the work runs at the end of that save, once its rows are written.
    /// Inside a transaction begun through <see cref="Database"/>, and inside a save the context gave a transaction of
    /// its own, it runs when the transaction has committed, before the after-commit handlers and in the order it was
    /// queued. A save that fails, a rollback to a savepoint and a rollback drop the work of the saves they undo.
    /// </para>
    /// <para>
    /// The work is waited for. Its failure undoes nothing and stops nothing: it is reported with the handlers'
    /// failures, in the <see cref="AfterCommitException"/> the call that committed throws. Work that runs at once throws
    /// from this call.
    /// </para>
    /// <para>
    /// The call is the caller's own, so no switch turns it off: it runs under <see cref="SuppressAfterCommit"/> too.
    /// </para>
    /// <para>
    /// Inside a transaction this context cannot follow (<c>UseTransaction</c>, <c>TransactionScope</c>) the call throws:
    /// the context is never told when such a transaction commits.
    /// </para>
    /// </remarks>
    public void AfterCommit(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);

        Queue(new QueuedWork(_ =>
        {
            work();
            return Task.CompletedTask;
        }));
    }

    /// <inheritdoc cref="AfterCommit(Action)"/>
    public void AfterCommit(Func<Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        Queue(new QueuedWork(_ => work()));
    }

    /// <summary>
    /// <inheritdoc cref="AfterCommit(Action)" path="/summary"/> The token is not the caller's: it is cancelled only when
    /// the host is stopping.
    /// </summary>
    /// <inheritdoc cref="AfterCommit(Action)" path="/remarks"/>
    public void AfterCommit(Func<CancellationToken, Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        Queue(new QueuedWork(work));
    }

    /// <summary>
    /// Work of a framework part that the saves of one unit share: every piece queued under the same key runs as one,
    /// where the first was queued, with the payloads of the saves that were not undone.
    /// </summary>
    internal void AfterCommitTogether(object key, object payload, Action<IReadOnlyList<object>> run) =>
        Queue(new QueuedWork(key, payload, run));

    private void Queue(QueuedWork work)
    {
        ResetIfNewLease();

        // A save that is not committed yet holds the work in its unit: its transaction's, or its own.
        if (saveFrame is { PastCommit: false } frame)
        {
            if (frame.Unseen)
                throw NotFollowed(UnseenTransaction() ?? "a transaction it cannot follow", "cannot hold work for after the commit", "");

            UnitOf(frame).Queue(work);
            return;
        }

        if (Database.Followed is { } transaction)
        {
            transaction.Unit.Queue(work);
            return;
        }

        // The steps after a commit are running: the work joins them.
        if (stageUnit is { State: SaveUnitState.AfterCommit } committed)
        {
            committed.Queue(work);
            return;
        }

        if (UnseenTransaction() is { } unseen)
            throw NotFollowed(unseen, "cannot hold work for after the commit", "");

        if (work.RunTogether is { } together)
            together([work.Payload!]);
        else
            SaveLifecycleSync.Wait(() => work.Run!(AfterCommitToken));
    }

    // The rows a save writes are the entries EF accepts once the save succeeds: Added and Modified become Unchanged,
    // Deleted becomes Detached. Collecting them then, rather than before the save, includes the rows that appear
    // during the write. A save that nothing listens to collects nothing.
    private void StartCollecting()
    {
        if (collectingSaves++ == 0)
            ChangeTracker.StateChanged += stateChangedInSave ??= OnStateChangedInSave;
    }

    private void StopCollecting()
    {
        if (--collectingSaves == 0)
            ChangeTracker.StateChanged -= stateChangedInSave;
    }

    // Runs inside EF, for every state change while a save that collects is writing. It only takes notes: no observer,
    // rule or handler is called from here, because code that saves or throws inside EF's accept loop corrupts the save.
    private void OnStateChangedInSave(object? sender, EntityStateChangedEventArgs e)
    {
        if (!collecting.On)
            return;

        var newState = e.NewState;

        // A row that becomes modified or deleted during the save (EF's own change detection, a cascade, an interceptor
        // of the app's, the owner of an owned type) still holds its values from before here. Once EF accepts it they
        // are gone, so this is the last moment to copy them.
        if (newState is EntityState.Modified or EntityState.Deleted)
        {
            if (collecting.Before)
                NoteBefore(e.Entry, newState, NeedsOf(e.Entry.Metadata), detected: false);

            return;
        }

        // Only these changes are EF accepting a write. Any other, such as a new row that an interceptor stopped
        // tracking (Added to Detached), wrote nothing.
        SavedRowChange? change = (e.OldState, newState) switch
        {
            (EntityState.Added, EntityState.Unchanged) => SavedRowChange.Added,
            (EntityState.Modified, EntityState.Unchanged) => SavedRowChange.Modified,
            (EntityState.Deleted, EntityState.Detached) => SavedRowChange.Deleted,
            _ => null,
        };

        if (change is null)
            return;

        var entity = e.Entry.Entity;
        var needs = NeedsOf(e.Entry.Metadata);

        // What the context remembers when the save is undone: a row of its own, not a part of an owner's row. A row
        // of a type with its own CLR type, which names a record the app knows. A shared-type row, such as the join
        // row of a many-to-many, only while the save has written no other.
        if (change != SavedRowChange.Deleted && needs.Owner is null && collecting.Written is not { Shared: false })
        {
            if (collecting.Written is null || !needs.IsShared)
                collecting.Written = new WrittenRow(entity, needs.IsShared);
        }

        // Only the rows someone asked for are kept: the ones an observer that listens wants, and the ones a handler
        // that runs after the write has and is not switched off for.
        if ((needs.Wanted & collecting.Listening) == 0 &&
            !(collecting.ForHandlers && needs.Handlers.AnyAfterWrite && HasAHandlerAfterTheWrite(needs.Handlers, collecting.Switches)))
            return;

        var before = default(RowBefore);

        if (change != SavedRowChange.Added)
            rowsBefore?.TryGetValue(entity, out before);

        RowsCollected++;

        (savedRows ??= new List<SavedRow>(collecting.Expected)).Add(new SavedRow(entity, needs.ClrType, change.Value)
        {
            Original = before.Original,
            Flags = needs.Owner is null ? before.Flags : SavedRowFlags.OwnedPart,
        });
    }

    private static bool HasAHandlerAfterTheWrite(HandlerPlan plan, SaveSwitches? switches) =>
        switches is null ||
        SaveSwitches.AnyOn(switches, plan.For(SaveStage.AfterSave)) ||
        SaveSwitches.AnyOn(switches, plan.For(SaveStage.BeforeCommit)) ||
        SaveSwitches.AnyOn(switches, plan.For(SaveStage.AfterCommit));

    // Whether an observer compares a modified row of this type with what it held before the save.
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

    // In the save's transaction, a failure of an observer fails the save.
    private async ValueTask ReportSavedAsync(SaveReport report, IShiftDbContextSaveObserver[] listeners, CancellationToken cancellationToken)
    {
        foreach (var observer in listeners)
            await observer.SavedAsync(this, report, cancellationToken);
    }

    // The new signals become events once the rows they belong to are committed: at the end of a save that needs no
    // transaction, and when the open transaction commits otherwise. A rollback drops them.
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
}
