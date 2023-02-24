using System.Collections.Generic;

namespace ShiftSoftware.ShiftEntity.Core
{
    public class ShiftEntityResponse<T>
    {
        public ShiftEntityResponse() { }
        public ShiftEntityResponse(T entity)
        {
            this.Entity = entity;
        }

        public Message? Message { get; set; }
        public T? Entity { get; set; }
        public System.Collections.Generic.Dictionary<string, object>? Additional { get; set; }
    }

    public class Message
    {
        public string Title { get; set; } = default!;

        public string? Body { get; set; }

        public List<Message> SubMessages  { get; set; }

        public Message() { }

        public Message(string title) { this.Title = title; }
        public Message(string title, string body) { this.Title = title; this.Body = body; }
        public Message(string title, string body, List<Message> subMessages) { this.Title = title; this.Body = body; this.SubMessages = subMessages; }
    }
}
