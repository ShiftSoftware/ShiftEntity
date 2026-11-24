using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace ShiftSoftware.ShiftEntity.EFCore;

/// <summary>The stages of a save that a handler takes part in.</summary>
internal enum SaveStage
{
    BeforeSave,
    AfterSave,
    BeforeCommit,
    AfterCommit,
}

/// <summary>
/// One declaration of a handler class: with the context, with a module or with the host. It carries the place the
/// class asked for among the handlers of a stage.
/// </summary>
internal sealed record SaveHandlerRegistration(Type HandlerType, int Order)
{
    private static readonly (Type Definition, SaveStage Stage)[] stageInterfaces =
    [
        (typeof(IBeforeSaveHandler<>), SaveStage.BeforeSave),
        (typeof(IAfterSaveHandler<>), SaveStage.AfterSave),
        (typeof(IBeforeCommitHandler<>), SaveStage.BeforeCommit),
        (typeof(IAfterCommitHandler<>), SaveStage.AfterCommit),
    ];

    /// <summary>The stage interfaces a class implements, with the entity type each is written for.</summary>
    public static List<(SaveStage Stage, Type RowType)> StagesOf(Type handlerType)
    {
        var stages = new List<(SaveStage, Type)>();

        foreach (var implemented in handlerType.GetInterfaces())
        {
            if (!implemented.IsGenericType)
                continue;

            var definition = implemented.GetGenericTypeDefinition();

            foreach (var (stageInterface, stage) in stageInterfaces)
                if (definition == stageInterface)
                    stages.Add((stage, implemented.GetGenericArguments()[0]));
        }

        // GetInterfaces has no promised order, and the order of a class's calls must not change between runs.
        stages.Sort((a, b) => a.Item1 != b.Item1
            ? a.Item1.CompareTo(b.Item1)
            : string.CompareOrdinal(a.Item2.FullName ?? a.Item2.Name, b.Item2.FullName ?? b.Item2.Name));

        return stages;
    }

    /// <summary>Whether the class is a check that the blanket switches leave on.</summary>
    public static bool IsGuard(Type handlerType) => handlerType.IsDefined(typeof(GuardAttribute), inherit: false);

    /// <summary>Refuses, where it is declared, a class that cannot be a handler.</summary>
    public static void Validate(Type handlerType, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(handlerType, parameterName);

        // An open generic class is closed over the entity types of the model, so it has one type parameter to close.
        var creatable = handlerType.IsClass && !handlerType.IsAbstract &&
            (!handlerType.ContainsGenericParameters || (handlerType.IsGenericTypeDefinition && handlerType.GetGenericArguments().Length == 1));

        var stages = creatable ? StagesOf(handlerType) : [];

        if (stages.Count == 0)
            throw new ArgumentException(
                $"{handlerType.Name} cannot be declared as a save handler. A handler is a class that can be created and that implements " +
                "IBeforeSaveHandler<T>, IAfterSaveHandler<T>, IBeforeCommitHandler<T> or IAfterCommitHandler<T>. An open generic class " +
                "has one type parameter.", parameterName);

        if (IsGuard(handlerType) && stages.Exists(stage => stage.Stage == SaveStage.AfterCommit))
            throw new ArgumentException(
                $"{handlerType.Name} is marked [Guard] and implements IAfterCommitHandler<T>. A guard is a check that can refuse a save, " +
                "and nothing after the commit can refuse one. Move what runs after the commit to a handler class of its own, or take " +
                "the mark off.", parameterName);
    }
}

/// <summary>One handler class in one host: what it is marked with, and how it is created.</summary>
internal sealed class SaveHandlerClass(Type type, ServiceLifetime? lifetime)
{
    private ObjectFactory? factory;

    public Type Type { get; } = type;

    public bool IsGuard { get; } = SaveHandlerRegistration.IsGuard(type);

    /// <summary>Whether the class reads <see cref="ChangedRow{TEntity}.Original"/> after the write.</summary>
    public bool NeedsOriginal { get; } = type.IsDefined(typeof(NeedsOriginalAttribute), inherit: true);

    /// <summary>
    /// How the host registered the class in its services, where that is known. Null for a class the host did not
    /// register, which the context creates itself for each call.
    /// </summary>
    public ServiceLifetime? Lifetime { get; } = lifetime;

    /// <summary>
    /// Whether the class can be created with nothing: its one public constructor takes no parameter. Such a handler
    /// needs no services. It is created with <c>new</c> where there are none, and so runs on any context, also one
    /// that a tool, a seeder or a test builds by hand. It reads what it needs from its rows' <c>Services</c>.
    /// </summary>
    public bool NeedsNoServices { get; } = type.GetConstructors() is [var only] && only.GetParameters().Length == 0;

    /// <summary>
    /// Whether the class can be created outside a scope: where the context has no services at all, or only the root
    /// provider's. A singleton is the root provider's to give. A class that needs nothing is created as it is.
    /// </summary>
    public bool CanBeCreatedWithoutAScope(bool anyServices) =>
        NeedsNoServices || (anyServices && Lifetime == ServiceLifetime.Singleton);

    /// <summary>
    /// Creates the handler from the services of the saving context. A class the host registered comes from its
    /// container, which owns it. Any other class is created here, with its constructor injected, and belongs to the
    /// call it was created for. Without a scope to create it from (no services, or the root provider's), a class
    /// that is not a singleton is created as it is: the context has made sure that it needs nothing.
    /// </summary>
    public object Create(IServiceProvider? services, bool servicesAreTheRoot, out bool owned)
    {
        if (services is null || (servicesAreTheRoot && Lifetime != ServiceLifetime.Singleton))
        {
            owned = true;
            return Activator.CreateInstance(Type)!;
        }

        if (services.GetService(Type) is { } registered)
        {
            owned = false;
            return registered;
        }

        owned = true;
        return (factory ??= ActivatorUtilities.CreateFactory(Type, Type.EmptyTypes))(services, null);
    }
}

/// <summary>One stage method of one handler class: the class, the stage, and the entity type the method is written for.</summary>
internal abstract class SaveHandlerCall(SaveHandlerClass handler, SaveStage stage, Type rowType, int order)
{
    public SaveHandlerClass Handler { get; } = handler;
    public Type HandlerType => Handler.Type;
    public SaveStage Stage { get; } = stage;
    public SaveStages StageFlag { get; } = (SaveStages)(1 << (int)stage);
    public Type RowType { get; } = rowType;
    public int Order { get; } = order;
    public bool IsGuard => Handler.IsGuard;
    public bool NeedsOriginal => Handler.NeedsOriginal;

    /// <summary>Its place among every call of the host: by order, then as declared.</summary>
    public int Rank { get; set; }

    /// <summary>The call as a message names it.</summary>
    public string Name => $"{HandlerType.Name} ({StageName(Stage)}, {RowType.Name})";

    public static string StageName(SaveStage stage) => stage switch
    {
        SaveStage.BeforeSave => "before save",
        SaveStage.AfterSave => "after save",
        SaveStage.BeforeCommit => "before commit",
        _ => "after commit",
    };

    /// <summary>Calls a stage after the write.</summary>
    public abstract ValueTask InvokeAsync(object handler, ShiftDbContext db, SaveUnit unit, SavedRowView rows, CancellationToken cancellationToken);

    /// <summary>Calls the before-save stage.</summary>
    /// <param name="switchedOff">The stages in which the save's switches keep its rows from this handler class.</param>
    public abstract ValueTask InvokeBeforeSaveAsync(object handler, ShiftDbContext db, SaveUnit unit, PendingRowView rows, bool isRepositorySave,
        SaveStages switchedOff, CancellationToken cancellationToken);

    public static SaveHandlerCall Create(SaveHandlerClass handler, SaveStage stage, Type rowType, int order) =>
        (SaveHandlerCall)Activator.CreateInstance(typeof(SaveHandlerCall<>).MakeGenericType(rowType), handler, stage, order)!;
}

internal sealed class SaveHandlerCall<TEntity>(SaveHandlerClass handler, SaveStage stage, int order)
    : SaveHandlerCall(handler, stage, typeof(TEntity), order) where TEntity : class
{
    public override ValueTask InvokeAsync(object handler, ShiftDbContext db, SaveUnit unit, SavedRowView rows, CancellationToken cancellationToken)
    {
        var typed = new SavedRows<TEntity>(db, unit, rows);

        return Stage switch
        {
            SaveStage.AfterSave => ((IAfterSaveHandler<TEntity>)handler).AfterSaveAsync(typed, cancellationToken),
            SaveStage.BeforeCommit => ((IBeforeCommitHandler<TEntity>)handler).BeforeCommitAsync(typed, cancellationToken),
            _ => ((IAfterCommitHandler<TEntity>)handler).AfterCommitAsync(typed, cancellationToken),
        };
    }

    public override ValueTask InvokeBeforeSaveAsync(object handler, ShiftDbContext db, SaveUnit unit, PendingRowView rows, bool isRepositorySave,
        SaveStages switchedOff, CancellationToken cancellationToken) =>
        ((IBeforeSaveHandler<TEntity>)handler).BeforeSaveAsync(new PendingRows<TEntity>(db, unit, rows, isRepositorySave, switchedOff), cancellationToken);
}

/// <summary>The calls that get the rows of one entity type, by stage, in the order they run.</summary>
internal sealed class HandlerPlan
{
    public static readonly HandlerPlan None = new([]);

    private readonly SaveHandlerCall[][] byStage;

    public HandlerPlan(SaveHandlerCall[] calls)
    {
        All = calls;

        byStage =
        [
            calls.Where(call => call.Stage == SaveStage.BeforeSave).ToArray(),
            calls.Where(call => call.Stage == SaveStage.AfterSave).ToArray(),
            calls.Where(call => call.Stage == SaveStage.BeforeCommit).ToArray(),
            calls.Where(call => call.Stage == SaveStage.AfterCommit).ToArray(),
        ];

        Any = calls.Length > 0;
        AnyAfterWrite = calls.Any(call => call.Stage != SaveStage.BeforeSave);
        NeedsOriginal = calls.Any(call => call.Stage != SaveStage.BeforeSave && call.NeedsOriginal);
    }

    /// <summary>Every call of the type, in the order they run within a stage.</summary>
    public SaveHandlerCall[] All { get; }

    public bool Any { get; }

    /// <summary>Whether a call runs after the write, so the save must collect the rows it writes.</summary>
    public bool AnyAfterWrite { get; }

    /// <summary>Whether a call that runs after the write reads what a row held before the save. Before save needs no copy.</summary>
    public bool NeedsOriginal { get; }

    public SaveHandlerCall[] For(SaveStage stage) => byStage[(int)stage];
}

/// <summary>
/// The handlers that are switched off for a save, as the switches stood when the save started. They hold for the
/// save's rows through to the commit, also when the scope that set them has ended by then.
/// </summary>
internal sealed class SaveSwitches
{
    /// <summary>The handler classes that <c>SuppressSaveHandler</c> names. A guard is switched off only here.</summary>
    public Type[] Named { get; init; } = [];

    /// <summary>The stages that <c>SuppressSaveHandlers</c> names.</summary>
    public SaveStages Stages { get; init; }

    /// <summary><c>SuppressAllSaveHandlers</c>: every handler in every stage, except the guards.</summary>
    public bool All { get; init; }

    /// <summary>
    /// <c>SuppressAfterCommit</c>: nobody is told about the save. No after-commit handler runs, and the framework's
    /// parts queue nothing for after the commit.
    /// </summary>
    public bool AfterCommit { get; init; }

    public static bool IsOff(SaveSwitches? switches, SaveHandlerCall call) => switches is not null && switches.Off(call);

    /// <summary>The stages in which the switches keep a save's rows from a handler class, by the rule of <see cref="Off"/>.</summary>
    public static SaveStages StagesOff(SaveSwitches? switches, SaveHandlerClass handler)
    {
        const SaveStages every = SaveStages.BeforeSave | SaveStages.AfterSave | SaveStages.BeforeCommit | SaveStages.AfterCommit;

        if (switches is null)
            return SaveStages.None;

        if (switches.Named.Length > 0 && Array.IndexOf(switches.Named, handler.Type) >= 0)
            return every;

        if (handler.IsGuard)
            return SaveStages.None;

        if (switches.All)
            return every;

        return switches.AfterCommit ? switches.Stages | SaveStages.AfterCommit : switches.Stages;
    }

    /// <summary>Whether one of the calls still runs under the switches.</summary>
    public static bool AnyOn(SaveSwitches? switches, SaveHandlerCall[] calls)
    {
        if (switches is null)
            return calls.Length > 0;

        foreach (var call in calls)
            if (!switches.Off(call))
                return true;

        return false;
    }

    private bool Off(SaveHandlerCall call)
    {
        if (Named.Length > 0 && Array.IndexOf(Named, call.HandlerType) >= 0)
            return true;

        if (call.IsGuard)
            return false;

        return All || (Stages & call.StageFlag) != 0 || (AfterCommit && call.Stage == SaveStage.AfterCommit);
    }
}

/// <summary>
/// The handlers of one model in one host, and which of them get the rows of each entity type: the ones the context
/// declares, the ones its modules declare, and the ones the host adds. Worked out once, the first time a context of
/// that model saves in that host.
/// </summary>
internal sealed class SaveHandlerRegistry
{
    private static readonly SaveHandlerRegistry Empty = new([]);

    // The hosts of this process, each with the registries of its models. A host is known by the scope factory of its
    // root provider, which every scope of the host hands out. A context without services has no host.
    private static readonly ConditionalWeakTable<object, Hosted> hosts = [];
    private static readonly object NoHost = new();

    private readonly SaveHandlerCall[] calls;
    private readonly ConcurrentDictionary<Type, HandlerPlan> plans = new();

    private SaveHandlerRegistry(SaveHandlerCall[] calls)
    {
        this.calls = calls;

        for (var i = 0; i < calls.Length; i++)
            calls[i].Rank = i;

        HasBeforeSave = calls.Any(call => call.Stage == SaveStage.BeforeSave);
        HasAfterWrite = calls.Any(call => call.Stage != SaveStage.BeforeSave);
        HasInTransactionStages = calls.Any(call => call.Stage is SaveStage.AfterSave or SaveStage.BeforeCommit);
    }

    public bool IsEmpty => calls.Length == 0;

    /// <summary>Whether any handler runs before save, so a save looks for the rows that are new in each round.</summary>
    public bool HasBeforeSave { get; }

    /// <summary>Whether any handler runs after the write, so a save collects the rows it writes.</summary>
    public bool HasAfterWrite { get; }

    /// <summary>Whether any handler runs after save or before commit, so a save must look at its rows before it writes.</summary>
    public bool HasInTransactionStages { get; }

    /// <summary>The calls of rows of this type. Worked out at the type's first save and kept.</summary>
    public HandlerPlan For(Type entityType) => plans.GetOrAdd(entityType, type =>
    {
        var mine = calls.Where(call => call.RowType.IsAssignableFrom(type)).ToArray();
        return mine.Length == 0 ? HandlerPlan.None : new HandlerPlan(mine);
    });

    /// <summary>The registry of a context's model in the host its services belong to.</summary>
    public static SaveHandlerRegistry Of(ShiftDbContext context, IServiceProvider? services)
    {
        var host = services is null ? NoHost : (object?)services.GetService<IServiceScopeFactory>() ?? services;

        return hosts.GetValue(host, static _ => new Hosted()).Of(context, services);
    }

    private sealed class Hosted
    {
        private readonly ConditionalWeakTable<IModel, SaveHandlerRegistry> byModel = [];

        public SaveHandlerRegistry Of(ShiftDbContext context, IServiceProvider? services)
        {
            var model = context.Model;

            if (byModel.TryGetValue(model, out var registry))
                return registry;

            lock (byModel)
            {
                if (!byModel.TryGetValue(model, out registry))
                    byModel.Add(model, registry = Build(context, model, services));

                return registry;
            }
        }
    }

    private static SaveHandlerRegistry Build(ShiftDbContext context, IModel model, IServiceProvider? services)
    {
        var declared = new List<SaveHandlerRegistration>();

        void Add(IEnumerable<SaveHandlerRegistration> more)
        {
            // The same class declared twice is one handler: the first declaration counts.
            foreach (var registration in more)
                if (!declared.Exists(known => known.HandlerType == registration.HandlerType))
                    declared.Add(registration);
        }

        // In this order, which is the order they run in when their order is the same: the context's own, the modules',
        // then the host's.
        var ofContext = new SaveHandlerSet();
        context.DeclareSaveHandlers(ofContext);
        Add(ofContext.Declared);

        SaveLifecycleHost? host = null;

        if (services is not null)
        {
            if (services.GetService<IEnumerable<IModelBuildingContributor>>() is { } contributors)
            {
                foreach (var contributor in contributors)
                {
                    var ofModule = new SaveHandlerSet();
                    contributor.ConfigureSaveHandlers(ofModule);
                    Add(ofModule.Declared);
                }
            }

            Add(services.GetServices<SaveHandlerRegistration>());
            host = services.GetService<SaveLifecycleHost>();
        }

        if (declared.Count == 0)
            return Empty;

        var built = new List<SaveHandlerCall>();
        var classes = new HashSet<Type>();

        foreach (var registration in declared)
        {
            foreach (var handlerType in Closed(registration.HandlerType, model))
            {
                if (!classes.Add(handlerType))
                    continue;

                var handler = new SaveHandlerClass(handlerType, host?.LifetimeOf(handlerType));

                foreach (var (stage, rowType) in SaveHandlerRegistration.StagesOf(handlerType))
                    built.Add(SaveHandlerCall.Create(handler, stage, rowType, registration.Order));
            }
        }

        // A stable sort: handlers with the same order run as declared.
        return new SaveHandlerRegistry(built.OrderBy(call => call.Order).ToArray());
    }

    // An open generic class stands for one handler for each entity type of the model that fits its constraints.
    private static IEnumerable<Type> Closed(Type handlerType, IModel model)
    {
        if (!handlerType.IsGenericTypeDefinition)
        {
            yield return handlerType;
            yield break;
        }

        var seen = new HashSet<Type>();

        foreach (var entityType in model.GetEntityTypes())
        {
            // A part of an owner's row, and a type that several entity types share, is not a row of its own.
            if (entityType.IsOwned() || entityType.HasSharedClrType || !seen.Add(entityType.ClrType))
                continue;

            Type closed;

            try
            {
                closed = handlerType.MakeGenericType(entityType.ClrType);
            }
            catch (ArgumentException)
            {
                // The entity type does not fit the class's constraints.
                continue;
            }

            yield return closed;
        }
    }
}

/// <summary>
/// A record as the saves of one unit know it: its entity type and key, so that the same record loaded again as another
/// instance is still the same record. A record whose key cannot be read off the instance stands for itself.
/// </summary>
internal readonly struct RowKey : IEquatable<RowKey>
{
    private readonly IEntityType? root;
    private readonly object? value;
    private readonly object?[]? values;

    private RowKey(IEntityType? root, object? value, object?[]? values)
    {
        this.root = root;
        this.value = value;
        this.values = values;
    }

    public static RowKey Of(IModel model, in SavedRow row)
    {
        // Null for a shared-type entity type, which a CLR type does not name.
        var type = model.FindEntityType(row.EntityType);

        if (type?.FindPrimaryKey() is not { } key)
            return new RowKey(null, row.Entity, null);

        var properties = key.Properties;

        if (properties.Count == 1)
            return properties[0].IsShadowProperty()
                ? new RowKey(null, row.Entity, null)
                : new RowKey(type.GetRootType(), properties[0].GetGetter().GetClrValue(row.Entity), null);

        var read = new object?[properties.Count];

        for (var i = 0; i < read.Length; i++)
        {
            if (properties[i].IsShadowProperty())
                return new RowKey(null, row.Entity, null);

            read[i] = properties[i].GetGetter().GetClrValue(row.Entity);
        }

        return new RowKey(type.GetRootType(), null, read);
    }

    public bool Equals(RowKey other)
    {
        if (root is null || other.root is null)
            return root is null && other.root is null && ReferenceEquals(value, other.value);

        if (!ReferenceEquals(root, other.root))
            return false;

        if (values is null || other.values is null)
            return values is null && other.values is null && Equals(value, other.value);

        if (values.Length != other.values.Length)
            return false;

        for (var i = 0; i < values.Length; i++)
            if (!Equals(values[i], other.values[i]))
                return false;

        return true;
    }

    public override bool Equals(object? obj) => obj is RowKey other && Equals(other);

    public override int GetHashCode()
    {
        if (root is null)
            return RuntimeHelpers.GetHashCode(value!);

        if (values is null)
            return HashCode.Combine(RuntimeHelpers.GetHashCode(root), value);

        var hash = new HashCode();
        hash.Add(RuntimeHelpers.GetHashCode(root));

        foreach (var part in values)
            hash.Add(part);

        return hash.ToHashCode();
    }
}

/// <summary>
/// A stage method that is running now, and the records it was called with. A save made from inside it does not call
/// it again for one of them.
/// </summary>
internal sealed class RunningHandler(SaveHandlerCall call, IReadOnlyList<SavedRow> rows, int[]? picked, int count)
{
    private HashSet<object>? records;
    private HashSet<RowKey>? keys;

    public SaveHandlerCall Call => call;

    public bool Handles(IModel model, in SavedRow row)
    {
        if (records is null)
        {
            records = new(ReferenceEqualityComparer.Instance);

            for (var i = 0; i < count; i++)
                records.Add(rows[picked is null ? i : picked[i]].Entity);
        }

        if (records.Contains(row.Entity))
            return true;

        // The same record as another instance, as after the change tracker was cleared.
        if (keys is null)
        {
            keys = [];

            for (var i = 0; i < count; i++)
                keys.Add(RowKey.Of(model, rows[picked is null ? i : picked[i]]));
        }

        return keys.Contains(RowKey.Of(model, row));
    }
}
