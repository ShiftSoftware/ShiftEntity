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
