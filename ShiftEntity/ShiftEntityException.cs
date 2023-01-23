using System.Net;

namespace ShiftSoftware.ShiftEntity.Core;

public class ShiftEntityException : System.Exception
{
    public Message Message { get; set; }
    public int HttpStatusCode { get; set; }

    public ShiftEntityException() {
        HttpStatusCode = (int)System.Net.HttpStatusCode.BadRequest;
    }

    public ShiftEntityException(
        string messageTitle,
        string messageBody,
        int httpStatusCode)
    {
        this.Message = new Message { Title = messageTitle, Body = messageBody };
        this.HttpStatusCode = httpStatusCode;
    }

    public ShiftEntityException(
        string messageTitle,
        string messageBody,
        HttpStatusCode httpStatusCode) :
        this(messageTitle, messageBody, (int) httpStatusCode)
    { }

    public ShiftEntityException(
        string messageTitle,
        string messageBody) :
        this(messageTitle, messageBody, System.Net.HttpStatusCode.BadRequest)
    { }
}
