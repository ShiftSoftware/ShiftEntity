using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ShiftSoftware.ShiftEntity.Core.Notifications;

/// <summary>What a host sets when it opts in with <c>AddNotifications</c>.</summary>
public sealed class NotificationOptions
{
    private readonly List<NotificationTopic> addedTopics = [];

    /// <summary>
    /// Whether a user needs a verified email address to get record content. On by default. An address on a subscribers
    /// list isn't affected, because it has no user.
    /// </summary>
    public bool RequireVerifiedEmail { get; set; } = true;

    /// <summary>The host's logo and footer, set once for every email.</summary>
    public NotificationEmailOptions Email { get; } = new();

    /// <summary>
    /// How long one event may take. After this its work is abandoned and logged, and the next event carries on, so a
    /// sender that hangs can't stop the queue. Two minutes by default.
    /// </summary>
    public TimeSpan EventTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Names the repository whose rules decide whether a user can open a record of this type. Needed when a type has more
    /// than one repository, because the check won't guess between them, or none registered as
    /// <see cref="IShiftEntityFind{EntityType}"/>, as with an attribute endpoint's built-in repository. The repository
    /// must be registered as itself, which <c>RegisterShiftRepositories</c> does.
    /// </summary>
    public NotificationOptions UseRepository<TEntity, TRepository>()
        where TEntity : ShiftEntity<TEntity>
        where TRepository : IShiftEntityFind<TEntity>
    {
        Repositories[typeof(TEntity)] = typeof(TRepository);
        return this;
    }

    /// <summary>The repositories named with <see cref="UseRepository{TEntity, TRepository}"/>, by entity type.</summary>
    internal Dictionary<Type, Type> Repositories { get; } = [];

    /// <summary>
    /// Raises the topic whenever a save creates a record of this type, through a repository or a plain
    /// <c>SaveChanges</c>, once the save has committed. One line in place of an after-save trigger.
    /// </summary>
    /// <remarks>
    /// <typeparamref name="TEntity"/> can also be a base class or an interface, and then every record that is one raises
    /// the topic. A record of a class derived from an entity is raised as that entity, so its email content and
    /// repository are the entity's. Register the topic in the same <c>AddNotifications</c> call or an earlier one. For
    /// other moments, see <see cref="IHasNotificationRules{TSelf}"/>, which also lists the saves that raise nothing, and
    /// <see cref="AddRule{TEntity, TRule}"/>.
    /// </remarks>
    public NotificationOptions RaiseWhenCreated<TEntity>(NotificationTopic topic) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(topic);
        CreatedRules.Add(new(typeof(TEntity), topic));
        return this;
    }

    /// <summary>
    /// Adds a rule class that says which topics a save raises, for a rule that needs services or applies to several entity
    /// types. This also registers the class in DI as a scoped service, unless it is registered already.
    /// </summary>
    public NotificationOptions AddRule<TEntity, TRule>()
        where TEntity : class
        where TRule : class, INotificationRule<TEntity>
    {
        RuleClasses.Add(new(typeof(TEntity), typeof(TRule)));
        return this;
    }

    /// <summary>The topics added with <see cref="RaiseWhenCreated{TEntity}"/>, each with its entity type.</summary>
    internal List<NotificationCreatedRule> CreatedRules { get; } = [];

    /// <summary>The rule classes added with <see cref="AddRule{TEntity, TRule}"/>, each with the entity type it is for.</summary>
    internal List<NotificationRuleClass> RuleClasses { get; } = [];

    /// <inheritdoc cref="AddTopics(Type)"/>
    public NotificationOptions AddTopics<TTopics>() where TTopics : class
        => AddTopics(typeof(TTopics));

    /// <summary>
    /// Registers the topics a class declares, including those in its nested classes. A topic is a public
    /// static field of type <see cref="NotificationTopic"/>, the way action trees declare their actions.
    /// Throws if the class declares none, because that is always a mistake.
    /// </summary>
    public NotificationOptions AddTopics(Type topicsType)
    {
        ArgumentNullException.ThrowIfNull(topicsType);

        var declared = GetDeclaredTopics(topicsType).ToList();

        if (declared.Count == 0)
            throw new InvalidOperationException(
                $"'{topicsType.Name}' declares no notification topics. Declare each one as a public static field of type {nameof(NotificationTopic)}.");

        addedTopics.AddRange(declared);

        return this;
    }

    /// <summary>The topics added since the last call. The registration moves them into the catalog.</summary>
    internal IReadOnlyList<NotificationTopic> TakeAddedTopics()
    {
        var taken = addedTopics.ToList();
        addedTopics.Clear();
        return taken;
    }

    private static IEnumerable<NotificationTopic> GetDeclaredTopics(Type type)
    {
        var own = type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(field => field.GetValue(null))
            .OfType<NotificationTopic>();

        return own.Concat(type.GetNestedTypes().SelectMany(GetDeclaredTopics));
    }
}

/// <summary>The parts of every email that belong to the host and not to one topic.</summary>
public sealed class NotificationEmailOptions
{
    /// <summary>
    /// The image at the top, or null for none. The sender decides how it reaches the inbox, so this is any value an img
    /// src accepts there: an absolute https URL or a cid: reference.
    /// </summary>
    public string? LogoUrl { get; set; }

    /// <summary>The text at the bottom, such as the company's name and address, or null for none.</summary>
    public string? Footer { get; set; }
}

/// <summary>A topic raised when a record of the entity type, or of one that is assignable to it, is created.</summary>
internal sealed record NotificationCreatedRule(Type EntityType, NotificationTopic Topic);

/// <summary>A rule class, and the entity type, base class or interface it is written for.</summary>
internal sealed record NotificationRuleClass(Type EntityType, Type RuleType);
