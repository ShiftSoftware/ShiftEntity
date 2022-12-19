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
    }
}
