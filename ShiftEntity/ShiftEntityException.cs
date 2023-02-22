using System.Collections.Generic;
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
        int httpStatusCode, List<Message> subMessage)
    {
        this.Message = new Message { Title = messageTitle, Body = messageBody, SubMessages = subMessage };
        this.HttpStatusCode = httpStatusCode;
    }

    public ShiftEntityException(
        string messageTitle,
        string messageBody,
        HttpStatusCode httpStatusCode, List<Message> subMessage) :
        this(messageTitle, messageBody, (int) httpStatusCode, subMessage)
    { }

    public ShiftEntityException(
        string messageTitle,
        string messageBody, List<Message> subMessage) :
        this(messageTitle, messageBody, System.Net.HttpStatusCode.BadRequest, subMessage)
    { }
}
