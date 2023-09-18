using System.Net;
namespace ShiftSoftware.ShiftEntity.Model;

public class HttpResponse<T>
{
    public HttpStatusCode StatusCode { get; set; }

    public string? ErrorMessage { get; set; }

    public T? Data { get; set; }

    public bool IsSuccess
    {
        get
        {
            return (int)StatusCode >= 200 && (int)StatusCode <= 299;
        }
    }

    public HttpResponse(T data, HttpStatusCode statusCode)
    {
        StatusCode = statusCode;
        Data = data;
    }

    public HttpResponse(string errorMessage, HttpStatusCode statusCode)
    {
        ErrorMessage = errorMessage;
        StatusCode = statusCode;
    }
}
