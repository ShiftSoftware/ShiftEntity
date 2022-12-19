namespace ShiftSoftware.ShiftEntity.Core
{
    public class ShiftEntityException : System.Exception
    {
        public Message Message { get; set; }
        public ShiftEntityException() { }
        public ShiftEntityException(string messageTitle, string messageBody)
        {
            this.Message = new Message { Title = messageTitle, Body = messageBody };
        }
    }
}
