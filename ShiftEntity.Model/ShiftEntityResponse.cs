
namespace ShiftSoftware.ShiftEntity.Model;

public class ShiftEntityResponse
{
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

    public List<Message>? SubMessages { get; set; }

    public Message() { }

    public Message(string title) { Title = title; }
    public Message(string title, string body) { Title = title; Body = body; }
    public Message(string title, string body, List<Message> subMessages) { Title = title; Body = body; SubMessages = subMessages; }
}
