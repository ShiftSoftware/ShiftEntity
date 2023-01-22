using System.Net;

namespace ShiftSoftware.ShiftEntity.Core
{
    public class ShiftEntityException : System.Exception
    {
        public Message Message { get; set; }
        public int HttpStatusCode { get; set; }

        public ShiftEntityException() {
            HttpStatusCode= 400;
        }

        public ShiftEntityException(
            string messageTitle,
            string messageBody,
            int httpStatusCode = 400)
        {
            this.Message = new Message { Title = messageTitle, Body = messageBody };
            this.HttpStatusCode = httpStatusCode;
        }

        public ShiftEntityException(
            string messageTitle,
            string messageBody,
            HttpStatusCode httpStatusCode = System.Net.HttpStatusCode.BadRequest) :
            this(messageTitle, messageBody, (int) httpStatusCode)
        { }
    }
}
