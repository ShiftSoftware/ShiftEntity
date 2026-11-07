using System;
using System.Collections.Generic;
using System.Linq;

namespace ShiftSoftware.ShiftEntity.Core.Notifications;

/// <summary>
/// A person attached to one record, and the topics they care about on it. A watcher is either an Identity user or a
/// customer. A user still has to pass the access checks. A customer gets only topics marked
/// <see cref="NotificationTopic.ForCustomers"/>, at the address the record holds.
/// </summary>
public sealed class NotificationWatcher
{
    private NotificationWatcher(long? userID, string? customerEmail, IReadOnlyList<NotificationTopic> topics)
    {
        UserID = userID;
        CustomerEmail = customerEmail;
        Topics = topics;
    }

    /// <summary>The user's ID, for a user watcher. Null for a customer.</summary>
    public long? UserID { get; }

    /// <summary>The address from the record, trimmed, for a customer watcher. Null for a user.</summary>
    public string? CustomerEmail { get; }

    /// <summary>The topics this person hears about on this record.</summary>
    public IReadOnlyList<NotificationTopic> Topics { get; }

    /// <summary>
    /// A user attached to this record, such as the person who created it. A record's user field is often empty, so
    /// null is allowed, and that watcher hears nothing.
    /// </summary>
    public static NotificationWatcher User(long? userID, params NotificationTopic[] topics)
        => new(userID, null, CheckTopics(topics));

    /// <summary>
    /// A customer attached to this record, at the address the record holds. A record's address is often empty, so null
    /// or blank is allowed, and that watcher hears nothing.
    /// </summary>
    public static NotificationWatcher Customer(string? email, params NotificationTopic[] topics)
        => new(null, string.IsNullOrWhiteSpace(email) ? null : email.Trim(), CheckTopics(topics));

    /// <summary>Whether this watcher hears about the topic with this ID.</summary>
    public bool Watches(string topicID) => Topics.Any(topic => topic.ID == topicID);

    private static NotificationTopic[] CheckTopics(NotificationTopic[] topics)
    {
        ArgumentNullException.ThrowIfNull(topics);
        return topics;
    }
}
