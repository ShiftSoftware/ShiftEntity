using System;
using System.Collections.Generic;
using System.Linq;

namespace ShiftSoftware.ShiftEntity.Core.Notifications;

/// <summary>
/// Someone attached to one record, and the topics they care about on it: an Identity user, an Identity team or a
/// customer. A user, and each member of a team, still has to pass the access checks. A customer gets only topics marked
/// <see cref="NotificationTopic.ForCustomers"/>, at the address the record holds.
/// </summary>
/// <remarks>
/// A watcher cares because of this record. Someone who cares because of a kind of news, whatever the record, belongs on
/// the subscribers list instead.
/// </remarks>
public sealed class NotificationWatcher
{
    private NotificationWatcher(long? userID, long? teamID, string? customerEmail, IReadOnlyList<NotificationTopic> topics)
    {
        UserID = userID;
        TeamID = teamID;
        CustomerEmail = customerEmail;
        Topics = topics;
    }

    /// <summary>The user's ID, for a user watcher. Null otherwise.</summary>
    public long? UserID { get; }

    /// <summary>The team's ID, for a team watcher. Null otherwise.</summary>
    public long? TeamID { get; }

    /// <summary>The address from the record, trimmed, for a customer watcher. Null otherwise.</summary>
    public string? CustomerEmail { get; }

    /// <summary>The topics this watcher hears about on this record.</summary>
    public IReadOnlyList<NotificationTopic> Topics { get; }

    /// <summary>
    /// A user attached to this record, such as the person who created it. A record's user field is often empty, so
    /// null is allowed, and that watcher hears nothing.
    /// </summary>
    public static NotificationWatcher User(long? userID, params NotificationTopic[] topics)
        => new(userID, null, null, CheckTopics(topics));

    /// <summary>
    /// A team attached to this record, such as the team it is assigned to. The members are read when the event is
    /// handled, and each one is checked like any user. A record's team field is often empty, so null is allowed, and that
    /// watcher hears nothing.
    /// </summary>
    public static NotificationWatcher Team(long? teamID, params NotificationTopic[] topics)
        => new(null, teamID, null, CheckTopics(topics));

    /// <summary>
    /// A customer attached to this record, at the address the record holds. A record's address is often empty, so null
    /// or blank is allowed, and that watcher hears nothing.
    /// </summary>
    public static NotificationWatcher Customer(string? email, params NotificationTopic[] topics)
        => new(null, null, string.IsNullOrWhiteSpace(email) ? null : email.Trim(), CheckTopics(topics));

    /// <summary>Whether this watcher hears about the topic with this ID.</summary>
    public bool Watches(string topicID) => Topics.Any(topic => topic.ID == topicID);

    private static NotificationTopic[] CheckTopics(NotificationTopic[] topics)
    {
        ArgumentNullException.ThrowIfNull(topics);
        return topics;
    }
}
