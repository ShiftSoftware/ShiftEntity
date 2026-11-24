using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Notifications;
using System.Collections.Concurrent;

namespace ShiftSoftware.ShiftEntity.EFCore.Notifications;

/// <summary>
/// Raises what the rules say about the rows a save of the host's context wrote: <see cref="NotificationOptions.RaiseWhenCreated{TEntity}"/>,
/// an entity's <see cref="IHasNotificationRules{TSelf}"/> and the classes added with
/// <see cref="NotificationOptions.AddRule{TEntity, TRule}"/>.
/// </summary>
/// <remarks>
/// <para>
/// It asks the context for values from before the save only for the types whose rules can compare them, so a save of
/// any other type costs nothing extra.
/// </para>
/// <para>
/// The rules run once the save has written and EF has accepted every row, before any other part of the framework and
/// before the after-save handlers, so each record still holds exactly what the save wrote and nothing is copied. No
/// rule runs inside EF. It never throws: a rule that fails is logged and the save carries on. The events it raises wait
/// for the save's commit, as any raise does.
/// </para>
/// <para>
/// A save under <see cref="ShiftDbContext.SuppressNotifications"/> or <see cref="ShiftDbContext.SuppressAfterCommit"/>
/// isn't reported to it. Under <see cref="ShiftDbContext.SuppressReplication"/> it still runs.
/// </para>
/// </remarks>
internal sealed class NotificationRulesSaveObserver(IServiceProvider services)
    : IShiftDbContextSaveObserver, ISavedRowOriginals, IOrderedSaveObserver, ISwitchedSaveObserver, IWaitsForCommitSaveObserver
{
    // SuppressNotifications. SuppressReplication doesn't silence the rules.
    public bool IsSuppressed(ShiftDbContext context) => context.NotificationsSuppressed;

    public string Work => "a notification rule";

    public string Switch => "SuppressNotifications()";

    // A rule raises an event, which waits for the commit. Whether it raises one is known only once the row is written.
    public bool WaitsFor(ShiftDbContext context, Type entityType, bool asChangedRoot) =>
        !asChangedRoot && RuleSet.DbContextType.IsInstanceOfType(context) && !RulesOrNone(RuleSet, entityType).IsEmpty;

    // Only the rows of the types that have a rule, saved by the context the host registered notifications on.
    public bool Wants(ShiftDbContext context, Type entityType) => WaitsFor(context, entityType, asChangedRoot: false);

    private NotificationRuleSet RuleSet => services.GetRequiredService<NotificationRuleSet>();

    private ILogger Logger => services.GetRequiredService<ILogger<NotificationRulesSaveObserver>>();

    public SaveObserverOrder Order => SaveObserverOrder.First;

    public bool NeedsOriginalOf(Type entityType)
    {
        // The context asks this inside the save. A failure here means the type's rules can't compare.
        try
        {
            return RuleSet.For(entityType).NeedsOriginal;
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "Could not work out the notification rules of {EntityType}. Its saves raise nothing.", entityType.Name);
            return false;
        }
    }

    public ValueTask SavedAsync(ShiftDbContext context, SaveReport report, CancellationToken cancellationToken)
    {
        // This runs inside the save, once the rows are written, so nothing here may throw: an error is logged and the
        // save carries on.
        try
        {
            var ruleSet = RuleSet;

            // Events are read back and checked on the context the host registered, so another context's saves raise nothing.
            if (!ruleSet.DbContextType.IsInstanceOfType(context))
                return ValueTask.CompletedTask;

            Type? lastType = null;
            var rules = NotificationTypeRules.None;
            ILogger? logger = null;
            NotificationTopicCatalog? catalog = null;
            NotificationQueue? queue = null;

            foreach (var row in report.Rows)
            {
                if (row.Change == SavedRowChange.Deleted)
                    continue;

                if (row.EntityType != lastType)
                {
                    lastType = row.EntityType;
                    rules = RulesOrNone(ruleSet, row.EntityType);
                }

                if (rules.IsEmpty)
                    continue;

                try
                {
                    logger ??= Logger;
                    var topics = rules.Evaluate(row, services, logger);

                    if (topics.Count == 0)
                        continue;

                    catalog ??= services.GetRequiredService<NotificationTopicCatalog>();
                    queue ??= services.GetRequiredService<NotificationQueue>();

                    foreach (var topic in topics)
                        rules.Raise(context, catalog, queue, logger, topic, row.Entity);
                }
                catch (Exception exception)
                {
                    Logger.LogError(exception, "Notification rules failed on a saved {EntityType}, so they raised nothing for it. The save isn't affected.",
                        row.EntityType.Name);
                }
            }
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "Notification rules failed on a save, so they raised nothing for it. The save isn't affected.");
        }

        return ValueTask.CompletedTask;
    }

    private NotificationTypeRules RulesOrNone(NotificationRuleSet ruleSet, Type entityType)
    {
        try
        {
            return ruleSet.For(entityType);
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "Could not work out the notification rules of {EntityType}. Its saves raise nothing.", entityType.Name);
            return NotificationTypeRules.None;
        }
    }
}

/// <summary>The rules a host added, and which of them apply to each entity type. One per host.</summary>
/// <remarks>
/// What applies to a type is worked out at its first save, after every <c>AddNotifications</c> call has run, and kept.
/// </remarks>
internal sealed class NotificationRuleSet(NotificationOptions options, Type dbContextType)
{
    private readonly ConcurrentDictionary<Type, NotificationTypeRules> byType = new();

    /// <summary>The context the host registered notifications on.</summary>
    public Type DbContextType { get; } = dbContextType;

    public NotificationTypeRules For(Type entityType) => byType.GetOrAdd(entityType, Build);

    private NotificationTypeRules Build(Type entityType)
    {
        var created = options.CreatedRules
            .Where(rule => rule.EntityType.IsAssignableFrom(entityType))
            .Select(rule => rule.Topic)
            .ToArray();

        var entityRules = entityType.GetInterfaces()
            .Where(type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IHasNotificationRules<>))
            .Select(type => (NotificationRuleInvoker)Activator.CreateInstance(
                typeof(EntityRuleInvoker<>).MakeGenericType(type.GetGenericArguments()[0]))!)
            .ToArray();

        var classRules = options.RuleClasses
            .Where(rule => rule.EntityType.IsAssignableFrom(entityType))
            .Select(rule => (NotificationRuleInvoker)Activator.CreateInstance(
                typeof(ClassRuleInvoker<>).MakeGenericType(rule.EntityType), [rule.RuleType])!)
            .ToArray();

        if (created.Length == 0 && entityRules.Length == 0 && classRules.Length == 0)
            return NotificationTypeRules.None;

        if (RaisedTypeOf(entityType) is not { } raisedType)
            return new UnraisableTypeRules(entityType);

        // The arguments go in one array: two arrays on their own would be read as arguments and activation attributes.
        object[] arguments = [created, entityRules.Concat(classRules).ToArray()];
        return (NotificationTypeRules)Activator.CreateInstance(typeof(NotificationTypeRules<>).MakeGenericType(raisedType), arguments)!;
    }

    // An event is raised on a ShiftEntity of its own type, which the pipeline can read back, and whose email content and
    // repository the host registered. For a row of a class derived from one (mapped to its table), that is the class it
    // derives from, as with replication.
    private static Type? RaisedTypeOf(Type entityType)
    {
        for (var type = entityType; type is not null && type != typeof(object); type = type.BaseType)
        {
            if (type.IsAbstract || type.ContainsGenericParameters || type.GetConstructor(Type.EmptyTypes) is null)
                continue;

            try
            {
                if (typeof(ShiftEntity<>).MakeGenericType(type).IsAssignableFrom(type))
                    return type;
            }
            catch (ArgumentException)
            {
            }
        }

        return null;
    }
}

/// <summary>The rules that apply to one entity type.</summary>
internal abstract class NotificationTypeRules
{
    public static readonly NotificationTypeRules None = new NoRules();

    /// <summary>Whether no rule applies, so the type's saves are skipped.</summary>
    public abstract bool IsEmpty { get; }

    /// <summary>Whether a rule about a change applies, which needs what a modified row held before the save.</summary>
    public abstract bool NeedsOriginal { get; }

    /// <summary>The topics this saved row raises, each once. Never throws.</summary>
    public abstract IReadOnlyList<NotificationTopic> Evaluate(SavedRow row, IServiceProvider services, ILogger logger);

    public abstract void Raise(ShiftDbContext db, NotificationTopicCatalog catalog, NotificationQueue queue, ILogger logger,
        NotificationTopic topic, object entity);

    private sealed class NoRules : NotificationTypeRules
    {
        public override bool IsEmpty => true;
        public override bool NeedsOriginal => false;
        public override IReadOnlyList<NotificationTopic> Evaluate(SavedRow row, IServiceProvider services, ILogger logger) => [];
        public override void Raise(ShiftDbContext db, NotificationTopicCatalog catalog, NotificationQueue queue, ILogger logger,
            NotificationTopic topic, object entity) { }
    }
}

internal sealed class NotificationTypeRules<TEntity>(NotificationTopic[] created, NotificationRuleInvoker[] rules) : NotificationTypeRules
    where TEntity : ShiftEntity<TEntity>, new()
{
    public override bool IsEmpty => false;

    public override bool NeedsOriginal => rules.Length > 0;

    public override IReadOnlyList<NotificationTopic> Evaluate(SavedRow row, IServiceProvider services, ILogger logger)
    {
        var isCreated = row.Change == SavedRowChange.Added;
        var topics = new List<NotificationTopic>();

        if (isCreated)
            topics.AddRange(created);

        if (rules.Length > 0)
        {
            if (!isCreated && row.Original is null)
            {
                // The context copies the old values of every row it sees become modified, so this is a row whose change it
                // could not see. There is nothing to compare with, and guessing could raise a topic on every save.
                logger.LogWarning("{EntityType} {RecordID} was saved as modified, but there are no values from before the save, so its notification rules are skipped for this save.",
                    typeof(TEntity).Name, ((TEntity)row.Entity).ID);
            }
            else
            {
                // The record still holds exactly what this save wrote: the rules run before anything that could change
                // it. A save that changes the record later is a save of its own, asked about on its own.
                var written = row.Entity;

                foreach (var rule in rules)
                {
                    try
                    {
                        topics.AddRange(rule.Invoke(services, written, row.Original, isCreated).Where(topic => topic is not null));
                    }
                    catch (Exception exception)
                    {
                        logger.LogError(exception, "{Rule} failed on {EntityType} {RecordID}, so it raises nothing on this save. The save isn't affected.",
                            rule.Name, typeof(TEntity).Name, ((TEntity)row.Entity).ID);
                    }
                }
            }
        }

        return topics.DistinctBy(topic => topic.ID).ToList();
    }

    public override void Raise(ShiftDbContext db, NotificationTopicCatalog catalog, NotificationQueue queue, ILogger logger,
        NotificationTopic topic, object entity)
        => NotificationRaiser.Raise(db, catalog, queue, logger, topic, (TEntity)entity, null);
}

/// <summary>
/// Rules apply to a type that neither is a ShiftEntity of its own type nor derives from one, so nothing can be raised on
/// it. Said once.
/// </summary>
internal sealed class UnraisableTypeRules(Type entityType) : NotificationTypeRules
{
    private int warned;

    public override bool IsEmpty => false;

    public override bool NeedsOriginal => false;

    public override IReadOnlyList<NotificationTopic> Evaluate(SavedRow row, IServiceProvider services, ILogger logger)
    {
        if (Interlocked.Exchange(ref warned, 1) == 0)
            logger.LogWarning("Notification rules apply to {EntityType}, but neither it nor a class it derives from is a ShiftEntity of its own type with a parameterless constructor, so nothing is raised on it.",
                entityType.Name);
        return [];
    }

    public override void Raise(ShiftDbContext db, NotificationTopicCatalog catalog, NotificationQueue queue, ILogger logger,
        NotificationTopic topic, object entity) { }
}

/// <summary>Calls one rule with the change typed the way the rule is written.</summary>
internal abstract class NotificationRuleInvoker
{
    public abstract string Name { get; }

    public abstract IEnumerable<NotificationTopic> Invoke(IServiceProvider services, object entity, object? original, bool isCreated);
}

internal sealed class EntityRuleInvoker<TRuleEntity> : NotificationRuleInvoker where TRuleEntity : class
{
    public override string Name => $"The notification rules of {typeof(TRuleEntity).Name}";

    public override IEnumerable<NotificationTopic> Invoke(IServiceProvider services, object entity, object? original, bool isCreated)
        => ((IHasNotificationRules<TRuleEntity>)entity).GetTopicsToRaise(Change(services, entity, original, isCreated))?.ToList() ?? [];

    internal static NotificationChange<TRuleEntity> Change(IServiceProvider services, object entity, object? original, bool isCreated)
        => new()
        {
            IsCreated = isCreated,
            Entity = (TRuleEntity)entity,
            Original = (TRuleEntity?)original,
            Services = services,
        };
}

internal sealed class ClassRuleInvoker<TRuleEntity>(Type ruleType) : NotificationRuleInvoker where TRuleEntity : class
{
    public override string Name => $"Notification rule {ruleType.Name}";

    public override IEnumerable<NotificationTopic> Invoke(IServiceProvider services, object entity, object? original, bool isCreated)
    {
        var rule = (INotificationRule<TRuleEntity>)services.GetRequiredService(ruleType);
        return rule.GetTopicsToRaise(EntityRuleInvoker<TRuleEntity>.Change(services, entity, original, isCreated))?.ToList() ?? [];
    }
}
