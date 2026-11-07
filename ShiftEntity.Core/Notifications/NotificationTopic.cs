using ShiftSoftware.TypeAuth.Core.Actions;

namespace ShiftSoftware.ShiftEntity.Core.Notifications;

/// <summary>
/// One kind of news a host can announce about a record, for example "an invoice was created".
/// A host declares its topics in code as public static fields and registers them with
/// <c>AddNotifications</c>. The registration checks every topic and fails at startup if one is wrong.
/// </summary>
public sealed class NotificationTopic
{
    /// <summary>
    /// The stable identity of the topic, qualified by the host's system name, for example
    /// <c>Sales.Invoice.Created</c>. Subscribers lists store this value, so do not change it when
    /// the wording of the topic changes.
    /// </summary>
    public required string ID { get; init; }

    /// <summary>The name people see. It can change at any time, because nothing refers to it.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Explains to people when this topic is raised.</summary>
    public required string Description { get; init; }

    /// <summary>
    /// The read permission of the screen that shows the record. A user who doesn't have it gets no
    /// email on this topic. Being on a list never grants it. Every topic needs one, except a
    /// <see cref="ForCustomers"/> topic, which can't have one because its recipients aren't users.
    /// </summary>
    public ActionBase? ReadPermission { get; init; }

    /// <summary>
    /// Whether a subscribers list may send this topic to plain email addresses, which have no user
    /// to check. Off by default. A <see cref="ForCustomers"/> topic can't turn this on.
    /// </summary>
    public bool AllowEmails { get; init; }

    /// <summary>
    /// Whether this topic is written for customers. Off by default. Such a topic goes only to the
    /// customers attached to the record, and a subscribers list can't name it.
    /// </summary>
    public bool ForCustomers { get; init; }
}
