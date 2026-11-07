using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace ShiftSoftware.ShiftEntity.Core.Notifications;

/// <summary>
/// The topics this host has registered. Use it to look up a topic by its ID or to list all of them.
/// <c>AddNotifications</c> registers it as a singleton, so a host that doesn't opt in doesn't have one.
/// The topics are added only during startup, and nothing can add one after that.
/// </summary>
public sealed class NotificationTopicCatalog
{
    private readonly List<NotificationTopic> topics = [];
    private readonly Dictionary<string, NotificationTopic> topicsByID = new(StringComparer.Ordinal);

    /// <summary>Creates an empty catalog. Topics are added through <c>AddNotifications</c>.</summary>
    internal NotificationTopicCatalog(string systemName)
    {
        if (!IsQualifiedName(systemName))
            throw new InvalidOperationException(
                $"'{systemName}' is not a valid notification system name. Use letters, digits, '_' and '-', with '.' between parts.");

        SystemName = systemName;
    }

    /// <summary>The host's system name. Every topic ID starts with this name.</summary>
    public string SystemName { get; }

    /// <summary>All the topics, in the order they were registered.</summary>
    public IReadOnlyList<NotificationTopic> Topics => topics;

    /// <summary>
    /// Tries to get the topic with exactly this ID. Returns false for an unknown ID, for example a
    /// topic that a subscribers list still names but the code no longer declares.
    /// </summary>
    public bool TryGetTopic(string id, [MaybeNullWhen(false)] out NotificationTopic topic)
    {
        if (id is null)
        {
            topic = null;
            return false;
        }

        return topicsByID.TryGetValue(id, out topic);
    }

    /// <summary>Checks the topic and adds it. Throws if the topic is wrong, so the host fails at startup.</summary>
    internal void Add(NotificationTopic topic)
    {
        Validate(topic);

        topics.Add(topic);
        topicsByID.Add(topic.ID, topic);
    }

    private void Validate(NotificationTopic topic)
    {
        if (!topic.ID.StartsWith(SystemName + ".", StringComparison.Ordinal) || !IsQualifiedName(topic.ID))
            throw new InvalidOperationException(
                $"The notification topic ID '{topic.ID}' is not valid in this host. It must start with the system name, " +
                $"like '{SystemName}.Invoice.Created', and use only letters, digits, '_' and '-', with '.' between parts.");

        // Lookups are exact, so two IDs that differ only in case would work. They are refused because
        // one of them is almost certainly a typing mistake.
        if (topics.Any(existing => string.Equals(existing.ID, topic.ID, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"The notification topic ID '{topic.ID}' is registered more than once.");

        if (string.IsNullOrWhiteSpace(topic.DisplayName) || string.IsNullOrWhiteSpace(topic.Description))
            throw new InvalidOperationException($"The notification topic '{topic.ID}' needs a display name and a description.");

        if (topic.ForCustomers)
        {
            if (topic.ReadPermission is not null)
                throw new InvalidOperationException(
                    $"The notification topic '{topic.ID}' is for customers, so it can't have a read permission. " +
                    "Nothing would check it, because customers aren't users.");

            if (topic.AllowEmails)
                throw new InvalidOperationException(
                    $"The notification topic '{topic.ID}' is for customers, so it can't allow emails. " +
                    "A subscribers list can't name a topic for customers.");
        }
        else if (topic.ReadPermission is null)
        {
            throw new InvalidOperationException(
                $"The notification topic '{topic.ID}' needs a read permission: the read permission of the screen that shows the record.");
        }
    }

    private static bool IsQualifiedName(string name)
    {
        return !string.IsNullOrEmpty(name)
            && name.Split('.').All(part => part.Length > 0 && part.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'));
    }
}
