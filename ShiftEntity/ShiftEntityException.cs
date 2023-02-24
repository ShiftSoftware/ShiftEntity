using System.Collections.Generic;

namespace ShiftSoftware.ShiftEntity.Core;

public class ShiftEntityException : System.Exception
{
    /// <summary>
    /// A Message to describe what went wrong.
    /// </summary>
    public new Message Message { get; set; }

    /// <summary>
    /// The HttpStatusCode to be returned as the Response Status
    /// </summary>
    public int HttpStatusCode { get; set; }

    /// <summary>
    /// A generic place holder for any additional data related to the error.
    /// </summary>
    public Dictionary<string, object> AdditionalData { get; set; }

    public ShiftEntityException(
        Message message,
        int httpStatusCode = (int)System.Net.HttpStatusCode.BadRequest,
        Dictionary<string, object> additionalData = null
    )
    {
        this.Message = message;
        this.HttpStatusCode = httpStatusCode;
        this.AdditionalData = additionalData;
    }
}