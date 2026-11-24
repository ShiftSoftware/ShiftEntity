
namespace ShiftSoftware.ShiftEntity.Model;

public class ShiftEntityResponse
{
    /// <summary>
    /// The key in <see cref="Additional"/> that marks the answer for a record that was saved although a step that runs
    /// after the commit failed. Its value is <see langword="true"/>. The status is the one of a successful save, the
    /// record is in the response, and <see cref="Message"/> says what failed. A client that is not a person tests for
    /// the key to tell "saved" from "saved, but" without reading text.
    /// </summary>
    public const string SavedWithFailure = "SavedWithFailure";

    public ShiftEntityResponse() { }

    public Message? Message { get; set; }
    public Dictionary<string, object>? Additional { get; set; }
}

public class ShiftEntityResponse<T> : ShiftEntityResponse
{
    public ShiftEntityResponse() : base() { }
    public ShiftEntityResponse(T entity)
    {
        Entity = entity;
    }

    public T? Entity { get; set; }
}

public class Message
{
    public string Title { get; set; } = default!;

    public string? Body { get; set; }

    // This property is used to indicate the entity or context this message is related to.
    // For example, it could be used for error messages related to a specific field.
    // In forms, for each SubMessages that has a "For" property,
    // it will use the "Title" in the inner SubMessage as the error message.
    public string? For { get; set; }

    public List<Message>? SubMessages { get; set; }

    public Message() { }

    public Message(string title) { Title = title; }
    public Message(string title, string body) { Title = title; Body = body; }
    public Message(string title, string body, List<Message> subMessages) { Title = title; Body = body; SubMessages = subMessages; }
}
